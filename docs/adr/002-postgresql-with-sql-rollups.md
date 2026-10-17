# ADR-002: PostgreSQL + EF Core, with SQL-side time-bucket rollups

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
The requirements name EF Core with "e.g. PostgreSQL or SQLite". The collector writes continuously while the API reads. The SMA endpoint rolls 1m candles up into periods as large as 1 week across up to ~2,000 buckets (ADR-006), which in the worst case covers about 20M rows.

## Decision
Use **PostgreSQL 17** through Npgsql EF Core, with **EF Core migrations** for the schema. EF/LINQ handles CRUD and simple reads. The SMA rollup is one **raw SQL query** using `date_bin` + `DISTINCT ON`, placed behind `ICandleQueryRepository` in Infrastructure.

## Options Considered

### Option A: SQLite
| Dimension | Assessment |
|---|---|
| Complexity | Low (no server) |
| Cost | Zero |
| Scalability | One writer at a time; no time-bucket functions |
| Team familiarity | High |

**Pros:** Zero-ops. Tests are trivial.
**Cons:** Writer/reader contention. Bucketing has to happen in C# (fetching up to 20M rows) or as awkward `strftime` arithmetic. No growth path.

### Option B: PostgreSQL + pure LINQ
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | Postgres container |
| Scalability | Weak: LINQ can't express "last row per bucket" efficiently, so rows are pulled into memory |
| Team familiarity | High |

**Pros:** Provider-agnostic.
**Cons:** Bad query plans or in-memory aggregation for large `p`.

### Option C: PostgreSQL + raw SQL rollup ✅
| Dimension | Assessment |
|---|---|
| Complexity | Medium (one hand-written query) |
| Cost | Postgres container |
| Scalability | Good; growth path to partitioning or TimescaleDB |
| Team familiarity | Medium–High |

**Pros:** One round-trip that returns at most 2,000 rows. Uses the PK index range scan.
**Cons:** Tied to Postgres. Integration tests need a real Postgres (Testcontainers).

### Option D: TimescaleDB from day one
**Pros:** Continuous aggregates make large-`p` queries O(buckets).
**Cons:** An extra extension and operational concept for a dataset that is ~190 MB/year at the current scale. Premature.

## Trade-off Analysis
Portability (A/B) is worth less than a correct, fast rollup. Swapping databases isn't a requirement, and the Postgres-specific part is one query behind an interface. D is the natural next step and needs no schema rewrite (same table becomes a hypertable), so deferring it costs little.

## Consequences
- **Easier:** concurrent ingest and reads, fast SMA, clear scaling path.
- **Harder:** tests need Docker. The SQL is outside LINQ's type safety, so integration tests have to cover it.
- **Revisit:** when `p=1w` p95 exceeds 500 ms or the number of symbols passes ~500, add 1h/1d rollup tables or a TimescaleDB continuous aggregate. Partition `candles_1m` by month if a retention policy is ever wanted (history is kept forever per Q2).

## Action Items
1. [ ] `MarketDataDbContext` with Npgsql, snake_case naming, initial migration.
2. [ ] `ICandleQueryRepository.GetBucketClosesAsync(symbolId, step, from, to, limit)` using the `date_bin` query (origin `2000-01-03Z`, a Monday).
3. [ ] Testcontainers-based integration tests for the rollup at week, day and minute boundaries.
