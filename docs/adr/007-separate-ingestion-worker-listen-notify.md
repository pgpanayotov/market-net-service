# ADR-007: Separate ingestion worker; Postgres LISTEN/NOTIFY for cross-process signals

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner
**Supersedes:** [ADR-003](003-collector-in-api-process.md)

## Context
ADR-003 ran the collector inside the API process and signalled symbol changes through an in-memory channel. That ties the API to a single replica, because a second replica would open a second WebSocket and race the first. It also puts the API and ingestion in the same failure domain: an API deploy or crash interrupts ingestion.

The owner decided to **split ingestion into its own process** and use **Postgres `LISTEN/NOTIFY`** for admin operations. One signal crosses a process boundary:
- **API → worker:** a symbol was added or removed (admin endpoints).

*Amendment (P3, 2026-10-18):* a second channel, `candles_written` (worker → API), was dropped before implementation. New candles arrive on a predictable schedule (once a minute per active symbol), so the API reads the newest one per request with a `max(open_time)` index probe and puts it in the cache key ([ADR-006](006-analysis-semantics.md)). This removes a listener connection, reconnect handling and in-memory state from every API replica.

## Decision
- **Two hosts:**
  - **`MarketData.Ingestion`** is a .NET Worker Service containing `BinanceStreamCollector`, `CandleWriter`, `GapBackfiller` and `SymbolChangeListener`. It runs as **one active instance**, guarded by `pg_try_advisory_lock(<const>)` held on a dedicated connection. An accidental second replica waits as a hot standby and takes over if the lock is released (process dies, connection drops).
  - **`MarketData.Api`** is stateless (apart from its local cache) and can scale horizontally. It never touches Binance WebSockets and holds no `LISTEN` connection. It calls Binance REST only for `exchangeInfo` validation on symbol add.
- **Channel `symbol_changes` (API → worker).** `SymbolsController` updates `symbols` and calls `SELECT pg_notify('symbol_changes', @json)` **in the same transaction**. Postgres delivers notifications only on commit, so a rolled-back change is never signalled.
  Payload: `{"op":"add"|"remove","symbolId":100,"symbol":"SOLUSDT","changedAt":"…"}`. *(Amended in P3: `symbolId` added and `version` replaced by `changedAt`. Receivers re-read the symbol from the DB, so `op` is only informational.)*
- **Notifications are hints; the DB is the source of truth.** `NOTIFY` is at-most-once: a listener that is disconnected misses messages. So:
  - The listener runs on a **dedicated, non-pooled `NpgsqlConnection`** (`KeepAlive=30`) looping on `WaitAsync(ct)`.
  - On every (re)connect the order is **first `LISTEN`, then reconcile from the DB**, so no change can fall between the two. The worker diffs active symbols against live subscriptions.
  - The worker also reconciles every 60s, so a silently missed message is limited in time.
  - Handlers are idempotent (re-read the symbol, then subscribe-if-absent or unsubscribe-if-present), so duplicates and reordering are harmless.
- **Shared code** (DbContext, Npgsql listener, Binance adapters) lives in `MarketData.Infrastructure`. The `PgNotificationListener` is a reusable `BackgroundService` parameterised by channel name and handler.

## Options Considered

### Option A: Single process, in-memory channel (ADR-003)
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | 1 container |
| Scalability | API pinned to 1 replica |
| Fault isolation | None |

**Pros:** Simplest. Instant signals.
**Cons:** No API scale-out. An API restart creates an ingestion gap (backfilled later, but avoidable).

### Option B: Separate worker, polling `symbols` table
| Dimension | Assessment |
|---|---|
| Complexity | Low–Medium |
| Cost | 2 containers |
| Scalability | Good |
| Fault isolation | Good |

**Pros:** No push infrastructure.
**Cons:** Changes are delayed up to the poll interval.

### Option C: Separate worker + Postgres `LISTEN/NOTIFY` ✅
| Dimension | Assessment |
|---|---|
| Complexity | Medium (listener loop, reconnect + reconcile) |
| Cost | 2 containers, +1 DB connection (the worker's listener) |
| Scalability | API scales out freely; one worker listens |
| Fault isolation | Good |

**Pros:** Sub-second propagation with no new infrastructure. Transactional with the admin write.
**Cons:** At-most-once delivery (handled by reconcile). Needs a session-level connection, so it **does not work through PgBouncer in transaction mode**. Payload ≤ 8 KB (ours is under 200 B).

### Option D: Separate worker + message broker (RabbitMQ / Redis pub/sub)
**Pros:** Durable queues, richer routing.
**Cons:** A third stateful service for a handful of tiny messages per minute, and still not transactional with the DB write without an outbox.

## Trade-off Analysis
C gives B's isolation and scale-out with A's responsiveness, using a database already in place. Its weakness, lost notifications during disconnects, is covered by the same rule already used for ingestion (ADR-005): **reconcile from the source of truth**, so missed messages only delay convergence and never break correctness. D would add durability we don't need, given that reconciliation exists anyway.

## Consequences
- **Easier:** scaling the API, deploying API changes without touching ingestion, and reasoning about failure (worker down means data stops growing but reads still work; API down means ingestion continues).
- **Harder:**
  - Two deployables and two health endpoints.
  - Admin `POST` is now **asynchronous**: 201 means "persisted", and subscription follows within about 1s. `GET /api/admin/symbols` exposes `lastCandleAt` so callers can see ingestion start.
  - Integration tests need both hosts or a fake notifier.
- **Constraint:** any connection pooler in front of Postgres must use session mode for the worker's listener connection (or let it bypass the pooler).
- **Revisit:** if the worker must shard (>1024 streams, ADR-001 scale limits), replace the single advisory lock with per-shard locks (`pg_try_advisory_lock(shardId)`) and assign symbols by hash.

## Action Items
1. [ ] Create `MarketData.Ingestion` (Worker SDK) and move collector, writer and backfiller hosted services there.
2. [x] `PgNotificationListener` in Infrastructure: dedicated connection, `LISTEN` → reconcile callback → `WaitAsync` loop, exponential reconnect.
3. [ ] `SymbolsController`: one transaction for the update and `pg_notify('symbol_changes', …)`.
4. [ ] Advisory-lock leadership in the worker. Readiness reports `leader | standby`.
5. [ ] Integration tests: admin add → worker subscribes (fake stream), and the listener reconnect path triggers reconcile.
