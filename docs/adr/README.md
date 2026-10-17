# Architecture Decision Records

Decisions behind [system-design.md](../system-design.md). New decisions get the next number. A decision that replaces another marks the old one `Superseded by ADR-xxx`.

| ADR | Title | Status |
|---|---|---|
| [001](001-store-1m-klines.md) | Store closed 1-minute klines as the canonical price data | Accepted |
| [002](002-postgresql-with-sql-rollups.md) | PostgreSQL + EF Core, with SQL-side time-bucket rollups | Accepted |
| [003](003-collector-in-api-process.md) | Host the collector inside the API process | Accepted, superseded by 007, 008 |
| [004](004-mvc-controllers-for-content-negotiation.md) | MVC controllers + XML formatters for content negotiation | Accepted |
| [005](005-self-healing-ingestion.md) | Self-healing ingestion: REST backfill + gap scan instead of a durable queue | Accepted |
| [006](006-analysis-semantics.md) | Analysis semantics: anchor at latest data, complete buckets, optional series | Accepted |
| [007](007-separate-ingestion-worker-listen-notify.md) | Separate ingestion worker; Postgres LISTEN/NOTIFY for cross-process signals | Accepted |
| [008](008-migration-bundle-init-container.md) | Apply schema with an EF Core migration bundle run as an init container | Accepted |

## Design review findings (2026-10-18)

The design was reviewed before implementation. These issues were found and are resolved in the ADRs above. [system-design.md](../system-design.md) has been updated to match.

| # | Finding | Severity | Resolution |
|---|---|---|---|
| R1 | Backfill resumes from `max(open_time)`. If the channel's `DropOldest` drops candles while newer ones are written, the **hole sits behind `max`** and is never refilled. | High | ADR-005: periodic gap scan with `generate_series` over the last 48h |
| R2 | The final kline lands a few seconds **after** the minute boundary. With `s = now`, the newest bucket can be read before its last candle is stored, so the SMA quietly uses a close that is 1 minute old. | High | ADR-006: `effectiveNow = min(s, latestStoredCandle.closeTime)` |
| R3 | The cache expires at the minute boundary, but the new candle arrives after it. A request at `hh:mm:00.5` caches stale data for a whole minute. | Medium | ADR-006: invalidate the cache on writer commit (per-symbol change token), not on a timer |
| R4 | "24h of **available** data" anchored at `now` returns 404 for a removed symbol whose history is kept (Q2 decision). | Medium | ADR-006: anchor the window at the latest stored candle |
| R5 | `AddProblemDetails()` writes **JSON only** for errors produced outside MVC (auth challenges, unmatched routes, exception handler). That breaks the "all responses JSON or XML" requirement. | Medium | ADR-004: custom XML `IProblemDetailsWriter` |
| R6 | The admin auth was described as an "endpoint filter", which is a Minimal-API concept that doesn't fit controllers. | Low | ADR-004: API-key `AuthenticationHandler` + `[Authorize(Policy = "Admin")]` |
| R7 | `count` defaulting to `n` makes `n=1000&series=true` an expensive default. | Low | ADR-006: default `count = min(n, 100)` |
| R8 | `MigrateOnStartup` races when more than one replica starts at once. | Low | ADR-008: `MigrateOnStartup` removed; an EF migration bundle runs as an init container |
