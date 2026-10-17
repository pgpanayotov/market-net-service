# Engineering Notes

Running log of approach, key technical decisions and validation steps.

## Approach

Design came first, then implementation:
1. [docs/requirements.md](docs/requirements.md) became the design in [docs/system-design.md](docs/system-design.md).
2. A self-review found 8 issues (R1–R8). They are recorded in [docs/adr/README.md](docs/adr/README.md).
3. Decisions are captured as ADRs. ADR-001, 002 and 004–008 are accepted, and ADR-003 is superseded by ADR-007 and ADR-008.
4. Work then follows the phases in [docs/implementation-plan.md](docs/implementation-plan.md) (P0–P9).

## Key decisions (summary — details in ADRs)

| Topic | Decision | ADR |
|---|---|---|
| Stored data | Closed 1-minute klines (`@kline_1m`); REST `/api/v3/klines` can replay them identically | 001 |
| Database | PostgreSQL 17 + EF Core; SMA rollup as one `date_bin` SQL query | 002 |
| HTTP | MVC controllers + DataContract XML formatters; XML ProblemDetails writer; API-key admin auth | 004 |
| Completeness | Idempotent upserts + tail backfill + 48h gap scan, rather than a durable queue | 005 |
| Analysis semantics | Anchored at the newest stored candle; complete buckets only; optional rolling series | 006 |
| Topology | Separate ingestion worker (advisory-lock leader); Postgres LISTEN/NOTIFY as hints + reconcile | 007 |
| Schema | EF migration bundle as init container; no `MigrateOnStartup`; seed through `HasData` | 008 |

## Phase log

### P0: Scaffold & conventions (2026-10-18)
- Solution is `MarketData.slnx` (the .NET 10 default XML solution format) with 6 `src` and 3 `tests` projects. The reference graph is Core → Contracts, Infrastructure → Core, Api/Ingestion → Infrastructure, and Client → Contracts only.
- `global.json` pins SDK `10.0.402` (`rollForward: latestFeature`) and opts `dotnet test` into **Microsoft.Testing.Platform**.
- `Directory.Build.props`: `net10.0`, nullable, `TreatWarningsAsErrors`, `AnalysisLevel=latest`, code style enforced in the build, NuGet lock files (locked mode when `ContinuousIntegrationBuild=true`).
- `Directory.Packages.props`: central package management, with versions resolved from nuget.org on 2026-10-18.
- `dotnet-ef` 10.0.13 is pinned in `.config/dotnet-tools.json`.
- CI is `.github/workflows/ci.yml`: tool restore, locked restore, Release build, and tests on `ubuntu-latest` (Docker is available for Testcontainers).

**Deviations and findings:**
- **xUnit v3 runs on Microsoft.Testing.Platform, not VSTest.** `xunit.v3` 4.x and the .NET 10 SDK refuse the VSTest path ("Testing with VSTest target is no longer supported…"). So `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` were dropped, and `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`. The test command syntax changes accordingly: `dotnet test --solution MarketData.slnx`, `dotnet test --project <path> --filter-class <FQN>`.
- An empty test project exits with MTP code 8 ("zero tests"). `tests/Directory.Build.props` passes `--ignore-exit-code 8` for now. **Remove it once each test project has tests.**
- `dotnet new tool-manifest` on .NET 10 creates `dotnet-tools.json` at the repo root, so it was moved to `.config/` to match the plan (both locations are discovered).
- Ingestion stays on `Microsoft.NET.Sdk.Worker` for now. It moves to the Web SDK (or adds a minimal Kestrel host) in P4, when its health endpoints on `:8081` are built.

**Validation:**
- `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
- `dotnet test --solution MarketData.slnx`: exit 0 (0 tests, as expected for P0).
- `dotnet ef --version`: 10.0.13 through the local tool manifest.
- Binance reachability from the dev network: `api.binance.com/api/v3/ping` → 200 and `data-api.binance.vision/api/v3/ping` → 200. Default endpoints are fine, but base URLs will still be configurable.

### P1: Core domain + Contracts (2026-10-18)

**What was built:**
- **Contracts:** `AvgPriceResponse`, `SmaResponse` (+ `SmaPoint`, `SmaSeriesItem`), `SymbolDto`, `SymbolList`, `AddSymbolRequest`.
  - Each is marked `[DataContract(Namespace = "")]` with explicit camelCase `DataMember` names and `Order`, so XML element names match the JSON property names.
  - Optional members use `EmitDefaultValue = false` together with `[JsonIgnore(WhenWritingNull)]`, so the single-value and series SMA shapes omit each other's fields in both formats.
- **Core / Domain:**
  - `TimePeriod`: parses period codes, floors to bucket start from the Monday origin `2000-01-03Z`, and works before the origin too.
  - `SymbolName`: normalizes and validates `^[A-Z0-9]{5,20}$`.
  - `MarketDataConstants`.
- **Core / Analysis:**
  - `SmaCalculator`: mean, plus the O(n + count) sliding-sum series over time windows.
  - `AnalysisValidator`: errors are keyed by query-parameter name (`symbol`, `n`, `p`, `s`, `count`), ready for ProblemDetails.
  - `PriceAnalysisService`: anchors at `dataEnd = latestOpenTime + 1m` and floors to `p` (ADR-006).
  - `AnalysisResult<T>` / `AnalysisError` (`Invalid`, `UnknownSymbol`, `NoData`).
- **Core / Abstractions:** the interfaces that P2 and P5 implement: `ISymbolRepository`, `ICandleQueryRepository`, `ISymbolDataState` (removed in P3; the newest candle is now read through `ICandleQueryRepository`).

**Decisions and deviations:**
- **DTO timestamps are UTC `DateTime`, not `DateTimeOffset`.** `DataContractSerializer` writes `DateTimeOffset` as a nested `<DateTime>/<OffsetMinutes>` element. A UTC `DateTime` is written as `2026-10-18T12:05:00Z` in both XML and JSON, and deserializes with `Kind = Utc` (covered by a test). Core still uses `DateTimeOffset` internally.
- **Period codes are case-sensitive.** The plan said case-insensitive, but `1M` means one month in Binance and common usage, so accepting it as one minute would be a silent mistake. The design doc and plan are updated.
- **Averages are rounded to 10 decimal places** (banker's rounding), the same as the `numeric(28,10)` price columns. Without rounding, `decimal` division would return up to 28 digits.
- **`isFullPeriod` = the first candle in the window is at the window start** (`[dataEnd − 24h, dataEnd)`), meaning the history covers the whole 24h. A gap exactly at the start minute would report `false`. That's acceptable, because the gap scan repairs such holes (ADR-005).
- **A start time `s` older than all stored data returns 404 "No data available"**, the same as a symbol with no candles.
- **Test-only setup:** `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) and `Microsoft.Testing.Extensions.CodeCoverage` were added to the test projects. `tests/Directory.Build.props` adds a global `using Xunit;`.
- **xUnit analyzer `xUnit1051`** (pass `TestContext.Current.CancellationToken`) is an error under `TreatWarningsAsErrors`. That applies even to NSubstitute setup calls, so tests pass the test token there too.

**Validation:**
- `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
- `dotnet test --project tests/MarketData.Core.Tests`: 97 passed.
- Coverage (`--coverage --coverage-output-format cobertura`): **100% line and branch on every hand-written Contracts and Core class**, including `TimePeriod`, `SmaCalculator`, `AnalysisValidator` and `PriceAnalysisService`. The only code under 100% is the regex source-generator output.
- Contract round-trips: every DTO serializes → deserializes → serializes identically through System.Text.Json (web defaults) and `DataContractSerializer`. Golden assertions pin the XML element names, the omission of unset members, and the JSON shape of a series response.

### P2: Persistence (2026-10-18)

**What was built:**
- **Core:** a `Candle` write model and `TimeRange`.
  - `ISymbolRepository` gained list, active, activate and deactivate operations (with outcome enums).
  - `ICandleQueryRepository` gained `GetLatestOpenTimesAsync`.
  - New `ICandleIngestRepository` covers upserts and the gap scan.
- **Infrastructure / Persistence:**
  - `MarketDataDbContext` (snake_case naming, `symbols` + `candles_1m`, composite PK, `numeric(28,10)`/`(38,10)`, FK `Restrict`).
  - `DesignTimeDbContextFactory`, the `Initial` migration with `HasData` seed, `SymbolRepository`, `CandleRepository`, `PendingMigrationsHealthCheck`, and `AddMarketDataPersistence` (DI; never migrates).
- **Hand-written SQL** runs through raw Npgsql commands on the context's connection, joined to its current transaction:
  - `INSERT … SELECT unnest(…) ON CONFLICT DO UPDATE … WHERE … IS DISTINCT FROM` for upserts.
  - `date_bin` + `DISTINCT ON` for SMA buckets.
  - `round(avg(close), 10)` for the 24h average.
  - `LATERAL max()` for the newest candle per symbol.
  - `generate_series` + `row_number()` grouping for the gap scan.

**Decisions and deviations:**
- **Raw Npgsql commands instead of `SqlQuery<T>`.** `SqlQuery<T>` maps columns by name, and how that interacts with `EFCore.NamingConventions` isn't obvious. Raw commands read by ordinal with no ambiguity. Because they enlist in `Database.CurrentTransaction`, the API can commit a symbol write and its `pg_notify` together (ADR-007), which a test covers.
- **Symbol activation is a single `INSERT … ON CONFLICT (name) DO UPDATE … WHERE NOT is_active RETURNING id, (xmax = 0)`.** Concurrent adds can't race into a unique violation (which would also abort the caller's transaction). The `xmax = 0` value tells a fresh insert apart from a reactivation, and no returned row means "already active".
- **Seeded rows reserve ids 1–99.** `HasData` sets ids explicitly, which doesn't advance the identity sequence, so the identity starts at 100 (`HasIdentityOptions(startValue: 100)`). Verified: the first admin-added symbol gets id 100.
- **Upserts dedupe by key within a batch** (last wins), because `ON CONFLICT DO UPDATE` errors when a statement touches the same row twice. **Unchanged rows are not rewritten** (`IS DISTINCT FROM`), so replays and backfills don't bloat the table.
- **The 24h average is rounded to 10 decimals in SQL.** An unrounded numeric `avg()` can exceed `System.Decimal` precision.
- **Gap scan:** minutes before a symbol's first candle are not gaps (that history is tail backfill's job), and a symbol with no candles returns no ranges. Bounds are floored to whole minutes.
- **Packages:**
  - `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 depends on EF Core 10.0.4 while EF Design is 10.0.13, which gave MSB3277 conflicts. Fixed with `CentralPackageTransitivePinningEnabled` and central pins for `Microsoft.EntityFrameworkCore` and `.Relational` 10.0.13.
  - Added `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` to Infrastructure.
- **New test project `tests/MarketData.Infrastructure.IntegrationTests`** (the plan put P2's tests nowhere specific). It uses one `postgres:17-alpine` container per run (xUnit v3 assembly fixture) and resets data before each test. Parallelization is off through `xunit.runner.json`, because `CollectionBehavior.DisableTestParallelization` is obsolete in xUnit 4.x.
- **The migration bundle is ~119 MB self-contained**, against ~70 MB estimated in ADR-008. Not a problem; image size can be trimmed in P7 if needed.
- **Running the bundle on `runtime-deps:10.0`** prints `libgssapi_krb5.so.2: cannot open shared object file` (Npgsql probing for Kerberos), but migrations succeed. **For P7:** install `libgssapi-krb5-2` in the migrator image, or accept the message.
- **CI** now also runs `dotnet ef migrations has-pending-model-changes`.

**Validation:**
- `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
- `dotnet test --solution MarketData.slnx`: **129 passed** (97 unit + 32 Postgres integration). The integration tests cover:
  - Upsert behaviour: idempotent replays, updates, in-batch duplicates, full precision of every column.
  - Half-open averaging with rounding.
  - 5m, day-boundary and Monday week-boundary buckets, with `limit`/range and newest-first order.
  - A property-style check that SQL `date_bin` buckets equal C# `TimePeriod.Floor` for all five periods over 200 random minutes.
  - Latest per symbol, and gap-scan range merging, start-at-first-candle and no-data cases.
  - The symbol lifecycle (created / already active / deactivated / already inactive / reactivated with the same id / not found).
  - Writes rolling back with the caller's transaction, and inactive symbols keeping their candles.
  - The health check: healthy when migrated, unhealthy with "Run the migrator" on an empty DB, unhealthy when unreachable.
- `dotnet ef migrations has-pending-model-changes`: no changes.
- **Migration bundle:** built for `linux-x64` and `win-x64`. The linux bundle was run twice in `mcr.microsoft.com/dotnet/runtime-deps:10.0` against a fresh `postgres:17-alpine` on a Docker network:
  - Run 1 applied `20261018160400_Initial` (with the EF migration lock).
  - Run 2 reported "No migrations were applied. The database is already up to date."
  - Both exited 0. Seed rows 1–3 are present, and a manual insert got id 100.

### P3: Postgres notifications (2026-10-18)

**What was built** (`src/MarketData.Infrastructure/Notifications`):
- `NotificationChannels` (`symbol_changes` only; see "Dropped `candles_written`" below).
- Payload record `SymbolChangedPayload(op, symbolId, symbol, changedAt)`, with a source-generated `NotificationJsonContext` (camelCase; enum values `add`/`remove`).
- `IPgNotificationPublisher` / `PgNotificationPublisher`: `SELECT pg_notify(@channel, @payload)` on the scoped DbContext connection, joined to its current transaction. It is registered in `AddMarketDataPersistence`.
- `PgNotificationListener`, an abstract `BackgroundService` with `OnConnectedAsync` (reconcile hook) and `OnNotificationAsync`:
  - Uses a dedicated session (`Pooling=false`, `KeepAlive=30`, `application_name=marketdata-listen-<channel>`).
  - Runs connect → `LISTEN` → reconcile → `WaitAsync` loop.
  - Reconnects with exponential backoff and ±20% jitter (configurable, 0.5s → 30s).
  - Exposes `IsListening` for readiness checks.
- `MarketDataMetrics` (meter `MarketData`): `marketdata.pg_listener.reconnects{channel}` and `marketdata.notifications.received{channel,outcome}`.

**Decisions and deviations:**
- **Dropped `candles_written`** (owner decision during P3 review, before commit). The worker → API channel would have fired once a minute per active symbol, an essentially predictable event. It only fed the API's in-memory "newest candle" value and cache invalidation.
  - **Replacement:** `ICandleQueryRepository.GetLatestOpenTimeAsync`, a `max(open_time)` backward PK index probe per request. The P5 cache key includes that value, with a 60s expiry and 1h for windows older than the 48h gap-scan window.
  - **Removed:** `ISymbolDataState`, `CandlesWrittenPayload`, and the planned `CandlesWrittenListener`, `SymbolDataState` and change-token plumbing. API replicas now hold no `LISTEN` connection.
  - **Trade-off:** one index probe per request instead of zero DB round-trips on a cache hit. Staleness after an out-of-order gap repair is bounded by the 60s cache expiry instead of being instant.
  - **Docs amended:** ADR-006 (anchor + caching), ADR-007 (one channel), the R3 resolution, system-design §3/§6.3/§6.4/§6.5/§7/§9/§11, the implementation plan (P4/P5) and CLAUDE.md.
- **Payload changes** (design doc and ADR-007 amended):
  - The payload carries `symbolId`, so the worker can re-read the symbol without a name lookup.
  - `symbol_changes` has `changedAt` instead of `version`.
  - Receivers should **re-read the symbol from the DB** rather than trust `op`, so ordering and duplicates don't matter.
- **Notifications are dispatched between `WaitAsync` calls, never inside Npgsql's `Notification` event.** The event only enqueues, so a handler can do async work (on other connections) without re-entering the listener connection.
- **Failure handling:**
  - A failing `OnConnectedAsync` counts as a failed connection and is retried with backoff (for example, the DB is up but the reconcile query fails).
  - A failing `OnNotificationAsync` is logged and counted (`outcome=error`), and the connection stays up.
- **Channel names are validated** against `^[a-z_][a-z0-9_]{0,62}$`, because `LISTEN` takes an identifier rather than a parameter. Payloads over 7999 UTF-8 bytes are rejected before reaching Postgres.
- **Metric names follow OpenTelemetry conventions** (`marketdata.*`) instead of the Prometheus-style names in the draft. A Prometheus exporter appends `_total` itself. system-design §7 is updated.
- **The listener's channel property is `ChannelName`.** Calling it `Channel` hid `System.Threading.Channels.Channel` in subclasses.
- Added `Microsoft.Extensions.Hosting.Abstractions` to Infrastructure (for `BackgroundService`).

**Validation:**
- `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
- `dotnet test --solution MarketData.slnx`: **137 passed** (97 unit + 40 Postgres integration). The 8 new `NotificationTests` cover:
  - A committed notify is delivered.
  - A rolled-back notify is never delivered (checked through commit ordering, without sleeping).
  - A notify inside a transaction is delivered on commit.
  - After `pg_terminate_backend` on the listener session, it reconnects, calls reconcile a second time, and receives later notifications.
  - A reconcile that throws on the first connect is retried.
- `dotnet test --solution MarketData.slnx`: **138 passed** (97 unit + 41 Postgres integration). The 8 `NotificationTests` cover:
  - The payload JSON shapes are stable.
  - Oversized payloads and invalid channel names are rejected.
- Flakiness check: `NotificationTests` were run 5 more times in a row, and all passed. Waits use channels with a 15s timeout, never fixed sleeps.
- `CandleRepositoryTests` gained a test for `GetLatestOpenTimeAsync` (newest candle of that symbol only; `null` without data). `PriceAnalysisServiceTests` now stub `ICandleQueryRepository.GetLatestOpenTimeAsync`; their results are unchanged.
