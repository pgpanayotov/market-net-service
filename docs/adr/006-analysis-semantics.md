# ADR-006: Analysis semantics: anchor at latest data, complete buckets, optional series

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
The requirements leave the exact semantics open:
- 24h average "over the last 24 hours **of available data** (or over the available period if less than 24 hours)".
- SMA with `n`, `p`, and an optional start reference `s` (default: now). The owner decided (Q1) that the SMA supports a single value **and** a rolling series through `?series=true`. Removed symbols keep their history (Q2).

The review found three timing problems with anchoring at wall-clock `now`:
- **R2:** the final kline for minute `t` arrives a few seconds after `t+1m`. A query in that window treats the newest bucket as complete but reads the previous minute's close.
- **R3:** a cache that expires on the minute boundary is filled before the new candle lands, and then serves stale data for ~60s.
- **R4:** a removed symbol still has history, but a `now`-anchored 24h window finds nothing and returns 404.

## Decision
1. **Anchor at the data, not the clock.** `latest = max(open_time) + 1m` for the symbol, read per request with a backward PK index probe (`ICandleQueryRepository.GetLatestOpenTimeAsync`). *(Amended in P3: originally an in-memory value fed by `candles_written` notifications; see ADR-007.)*
   - 24h average: window `[latest − 24h, latest)`. `isFullPeriod = false` when fewer than 24h exist. The response states `from`/`to`, so the anchor is visible.
   - SMA: `effectiveRef = floor(min(s ?? now, latest), p)`. A bucket counts only when it is complete in storage, never a half-written one.
2. **Bucket value** is the close of the bucket's last 1m candle. Days start at 00:00 UTC. Weeks start Monday 00:00 UTC (date_bin origin `2000-01-03Z`, the same as Binance `1w`).
3. **Single value (default):** mean of the last `n` bucket closes before `effectiveRef`, plus `dataPointsUsed` and optional `points`.
4. **Series (`series=true`):** `count` values (default **`min(n, 100)`**, max 1000, fixes R7) in ascending time order. Each is SMA(n) ending at its bucket. One query fetches `n + count − 1` buckets, and the series is computed with an O(n + count) sliding `decimal` sum. Elements with 0 points are omitted. The last element equals the single-value result.
5. **Cache keyed on the data version (fixes R3).** The cache key is `(endpoint, symbol, parameters, latestOpenTime)`. Each request first reads `latestOpenTime` (one index probe), so a newly stored candle automatically means a new key and nothing has to be invalidated. Entries expire 60s after creation, which bounds how long a gap repaired *behind* the newest candle (ADR-005) can be served stale. Results whose window ends more than 48h before `latest` (outside the gap-scan window) don't change, and are cached for 1h (sliding). *(Amended in P3: replaced per-symbol change tokens fired by `candles_written` notifications.)*
6. **Status codes:** unknown symbol → 404 `"Unknown symbol"`. Known symbol with no data at all → 404 `"No data available"`. Partial data → 200 with `dataPointsUsed`/`isFullPeriod`.

## Options Considered

### Option A: Anchor at wall-clock `now` (first draft)
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Correctness | R2 and R4 failures; stale cache (R3) |
| Predictability | Values change based on ingest timing |

**Pros:** Simple. "Last 24h" means exactly the last 24h of real time.
**Cons:** Silent mistakes on the newest bucket. Removed symbols return nothing.

### Option B: Anchor at `now`, wait or poll until the last candle lands
**Pros:** Wall-clock semantics without the R2 error.
**Cons:** Adds latency to requests. Breaks completely if the feed is down.

### Option C: Anchor at latest stored data ✅
| Dimension | Assessment |
|---|---|
| Complexity | Low (one index probe per request) |
| Correctness | Each bucket counts only when complete; deterministic for a given DB state |
| Predictability | High; the response shows `from`/`to`/`referenceTime` |

**Pros:** Fixes R2 and R4. Matches "of available data" literally. Results are stable and cacheable.
**Cons:** When the feed is stalled, results quietly describe an older window. This is mitigated by exposing the window in the response and by the staleness health check (ADR-005).

## Trade-off Analysis
For an analysis API, *correct and explainable* beats *wall-clock fresh*. C trades at most ~1 minute of recency for exactness and determinism, which also makes caching trivial (the result changes only when data changes). The stall risk is an operations problem, and monitoring is the right place to handle it.

## Consequences
- **Easier:** deterministic tests (fixed DB contents give a fixed answer regardless of the clock), caching without invalidation (the anchor is part of the key), removed symbols remain fully analysable.
- **Harder:** clients must read `to`/`referenceTime` instead of assuming "now". This is documented in the README and the client prints it.
- **Revisit:** if consumers need "as of now, even if stale", add `?anchor=now`. Bucket alignment for non-UTC days would need a `tz` parameter.

## Action Items
1. [x] `TimePeriod` value object: parse, `Floor(DateTimeOffset)`, `ToTimeSpan()`, Monday week origin.
2. [x] `PriceAnalysisService.Get24hAverageAsync` / `GetSmaAsync(n, p, s, series, count)` using `latest` from `ICandleQueryRepository.GetLatestOpenTimeAsync`.
3. [ ] API cache keyed by `latestOpenTime`: 60s absolute expiry, 1h sliding for windows older than 48h.
4. [x] Unit tests: `s` on and off boundaries, `s > latest`, week boundary, gaps, series with partial windows at the start of history, `count` without `series` → 400.
5. [ ] README: document A1/A2 and the anchoring rule with an example.
