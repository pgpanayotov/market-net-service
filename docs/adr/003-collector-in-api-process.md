# ADR-003: Host the collector inside the API process

**Status:** Accepted, then superseded by [ADR-007](007-separate-ingestion-worker-listen-notify.md) (process topology) and [ADR-008](008-migration-bundle-init-container.md) (migrations)
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
Two workloads exist: a long-lived WebSocket collector, and a request/response HTTP API. Admin endpoints must change the monitored symbols **at runtime**, so the API has to tell the collector about the change. The project is assignment-scale: one developer and one `docker compose up`.

## Decision
Run the collector as `BackgroundService`s (`BinanceStreamCollector`, `CandleWriter`, `GapBackfiller`) **inside the ASP.NET Core API host**. Admin changes are committed to the DB first and then signalled in-process through `Channel<SymbolCommand>`. The collector also reconciles DB state against its live subscriptions every 60s. Collector logic lives in Infrastructure behind `IMarketDataStream`, so it can move to a separate Worker host without code changes.

## Options Considered

### Option A: Single process ✅
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | One container |
| Scalability | API limited to 1 replica (collector must be singleton) |
| Team familiarity | High |

**Pros:** Instant in-process signal. One deployable and one health endpoint.
**Cons:** API and collector share failure domain and resources.

### Option B: Separate Worker + API, DB polling for symbol changes
| Dimension | Assessment |
|---|---|
| Complexity | Medium |
| Cost | Two containers |
| Scalability | API scales freely |
| Team familiarity | High |

**Pros:** Clean separation. API replicas are stateless.
**Cons:** Symbol changes apply only after the polling interval (seconds of latency). Two images, configs and health checks.

### Option C: Separate processes + message broker (RabbitMQ/Redis pub/sub)
**Pros:** Immediate cross-process signals.
**Cons:** A third piece of infrastructure to push one tiny event at a rate of a few per day. Over-engineered.

## Trade-off Analysis
Nothing in the requirements needs independent scaling, and admin changes are rare. A minimizes moving parts and gives the best UX for runtime symbol changes. The 60s reconciliation means correctness never depends on the in-process signal, which is exactly the property needed to move to B later (B's polling *is* that reconciliation loop). Postgres `LISTEN/NOTIFY` can then replace the channel, so a broker (C) is never needed.

## Consequences
- **Easier:** deployment, local dev, testing with `WebApplicationFactory` (the whole system in one host).
- **Harder:** horizontal API scaling. A second replica would open a second WS connection and duplicate writes (harmless because of idempotent upserts, but wasteful) and race on startup migrations (review R8).
- **Revisit:** before running more than one replica. Either split out the Worker (B + `LISTEN/NOTIFY`) or elect a leader with `pg_try_advisory_lock`. At the same point, switch `MigrateOnStartup` off in favour of an EF migration bundle run as an init container.

## Action Items
1. [ ] `ISymbolRegistry` (DB-backed) + `Channel<SymbolCommand>` registered as a singleton.
2. [ ] Collector applies per-symbol subscribe/unsubscribe and runs a 60s reconcile loop.
3. [ ] `Database:MigrateOnStartup` runs before hosted services start. Document it as single-instance only.
4. [ ] Collector settings in `MarketData:*` options (initial symbols, reconcile interval, backfill window).
