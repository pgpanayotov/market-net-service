# ADR-005: Self-healing ingestion: REST backfill + gap scan instead of a durable queue

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
The WebSocket feed is lossy by nature. Binance force-closes connections every 24h, networks drop, and the process restarts. Between the WS callback and the DB sits an in-memory `Channel<Candle>` (bounded, `DropOldest`), so a long DB outage also loses candles. The analysis endpoints are only correct if the 1m series has no gaps.

The first draft backfilled from `max(open_time)` per symbol. **Review R1:** if older candles are dropped while newer ones are written, the hole lies behind `max(open_time)` and is never repaired.

## Decision
Make the store **converge to complete** instead of trying to make delivery reliable:
1. All writes are idempotent upserts on `(symbol_id, open_time)` (ADR-001).
2. **Tail backfill:** at startup, on WS `ConnectionRestored`, and on symbol add, fetch REST `/api/v3/klines` from `max(open_time) + 1m` to the last closed minute. On add, the start is capped at `MarketData:BackfillOnAdd` (default 24h).
3. **Gap scan (new, fixes R1):** every 5 minutes, and immediately after the writer recovers from a DB error or reports drops, run per active symbol:
   ```sql
   SELECT m AS missing
   FROM generate_series(date_trunc('minute', now() - interval '48 hours'),
                        date_trunc('minute', now()) - interval '2 minutes',
                        interval '1 minute') m
   LEFT JOIN candles_1m c ON c.symbol_id = @id AND c.open_time = m
   WHERE c.open_time IS NULL AND m >= @firstCandle;
   ```
   Contiguous missing minutes are merged into ranges and fetched with REST (≤1000 per request).
4. The `GapBackfiller` is a single low-priority worker. It respects `Retry-After` on 429/418, and its work is resumable because every run recomputes the gaps.

## Options Considered

### Option A: Durable outbox / broker between WS and DB
| Dimension | Assessment |
|---|---|
| Complexity | High (Kafka/RabbitMQ or a local disk queue) |
| Cost | Extra infrastructure |
| Scalability | Good |
| Team familiarity | Medium |

**Pros:** No loss once a message is accepted.
**Cons:** Doesn't cover WS downtime, the biggest source of gaps, because nothing arrives to be queued. Lots of infrastructure for 3 messages a minute.

### Option B: Tail backfill only (first draft)
**Pros:** Simple.
**Cons:** Misses holes behind `max(open_time)` (R1), and also holes from manual DB edits or a partial batch failure.

### Option C: Tail backfill + periodic gap scan ✅
| Dimension | Assessment |
|---|---|
| Complexity | Medium (one extra query + range merging) |
| Cost | A cheap 48h scan: 2,880 index probes per symbol every 5 min |
| Scalability | ~1,000 symbols before the scan needs batching |
| Team familiarity | High |

**Pros:** Repairs **any** gap within 48h regardless of cause, and every run starts from scratch. The channel can drop safely.
**Cons:** Correctness depends on Binance REST availability and retention (unlimited for 1m klines today). Gaps older than 48h need a manual or admin-triggered rescan.

## Trade-off Analysis
Delivery guarantees (A) protect the wrong segment: most loss happens *before* data reaches us. Because Binance can replay exact klines, we can be lossy in transit and still end up complete. C adds one query to B and removes B's blind spot.

## Consequences
- **Easier:** the channel, writer and reconnect logic stay simple. Restarts and DB outages need no operator action.
- **Harder:** two code paths write the same rows (WS and REST). The mapping must be identical, and is tested through a shared `KlineMapper`.
- **Revisit:** if a source without replay is ever added, this approach doesn't apply and an outbox becomes necessary. Expose `POST /api/admin/symbols/{symbol}/rescan?from=` if older gaps are seen in practice.

## Action Items
1. [x] `GapBackfiller` with tail mode and gap-scan mode. Range merging is unit-tested.
2. [x] Writer reports `CandlesDropped` and `DbRecovered` events that trigger an immediate scan.
3. [ ] *(Metrics done in P4 as `marketdata.backfill.candles` and `marketdata.gap.minutes`; the readiness rule below is still open.)* Metrics `backfill_candles_total{symbol,reason}` and `gap_minutes{symbol}`. Readiness fails if `gap_minutes > 5` for more than 10 min.
4. [x] Integration test: drop candles in the middle of a series, then verify the scan restores them through a stubbed REST client.
