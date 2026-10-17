# ADR-001: Store closed 1-minute klines as the canonical price data

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
The service must collect prices from the Binance Spot multi-stream WebSocket and answer two kinds of question: a 24h average, and an SMA over buckets of `1m, 5m, 30m, 1d, 1w`. Binance offers several stream types at very different volumes. Whatever we store sets the size of the data, the cost of every query, and whether a gap can be repaired after the fact.

## Decision
Subscribe to `<symbol>@kline_1m` on the combined-stream endpoint and persist **only closed candles** (`k.x == true`) to `candles_1m (symbol_id, open_time) PK` as OHLCV + quote volume + trade count, using `numeric` columns that map to C# `decimal`.

## Options Considered

### Option A: Raw trades (`@trade` / `@aggTrade`)
| Dimension | Assessment |
|---|---|
| Complexity | High: needs batching at high throughput, and every query must aggregate ticks |
| Cost | ~100k–2M rows/symbol/day |
| Scalability | Poor at 100 symbols without TimescaleDB-style tooling |
| Team familiarity | Medium |

**Pros:** Full resolution. A true trade-weighted average is possible.
**Cons:** Huge volume for questions that need minute resolution at best. Backfill through REST `aggTrades` is slow and rate-limited.

### Option B: 1s snapshots (`@miniTicker`)
| Dimension | Assessment |
|---|---|
| Complexity | Medium |
| Cost | 86,400 rows/symbol/day |
| Scalability | OK |
| Team familiarity | High |

**Pros:** Simple "last price" semantics.
**Cons:** 60× more rows than A–C need. No REST equivalent, so gaps are permanent. Bucket boundaries are approximate.

### Option C: Closed 1m klines (`@kline_1m`) ✅
| Dimension | Assessment |
|---|---|
| Complexity | Low: about 1 message per minute per symbol is persisted |
| Cost | 1,440 rows/symbol/day (~170 KB) |
| Scalability | 100 symbols ≈ 52M rows/year, fine for plain Postgres |
| Team familiarity | High (Binance.Net typed models) |

**Pros:** Every requested period is an exact multiple of 1m. Identical data is available from REST `/api/v3/klines`, which is what makes ADR-005 possible. Writes are idempotent by PK.
**Cons:** No sub-minute resolution. "Average price" becomes time-weighted (mean of closes) rather than trade-weighted.

## Trade-off Analysis
The deciding factor is **repairability**. Only klines have a REST twin with identical content, so only C lets the store converge to "complete" after any outage. The resolution lost (below 1m) isn't used by any requirement. If a weighting question comes up, `quote_volume / volume` gives VWAP from the same rows.

## Consequences
- **Easier:** small DB, cheap queries, exact buckets, idempotent backfill.
- **Harder:** a future sub-minute feature needs a second table and stream.
- **Revisit:** if a trade-weighted 24h average is required, use the VWAP column. If tick analytics are required, add `@aggTrade` alongside rather than replacing this.

## Action Items
1. [ ] `Candle` entity + `candles_1m` migration with composite PK and `numeric(28,10)` prices.
2. [ ] Binance.Net `SubscribeToKlineUpdatesAsync(symbols, OneMinute)`; filter `Final`.
3. [ ] Upsert via `INSERT … ON CONFLICT DO UPDATE`.
4. [ ] Record assumption A1 (time-weighted average) in the README.
