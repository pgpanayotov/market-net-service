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

### P4: Ingestion worker (2026-10-18, parallel session, branch `claude/p4-ingestion-491741`)

Full log: [docs/phase-notes/P4.md](docs/phase-notes/P4.md).

**What was built:**
- **Binance adapters** in `Infrastructure/Binance`:
  - Configurable endpoints (`MarketData:Binance:RestBaseUrl`/`SocketBaseUrl`).
  - One `KlineMapper` for WS and REST, with golden files proving identical `Candle`s.
  - `IMarketDataStream` with per-symbol idempotent subscribe/unsubscribe and once-per-socket lost/restored events.
  - `IKlineRestClient` with paging, a shared `Retry-After` block on 429/418, and transient retries.
- **Worker** (now on the Web SDK, health on `:8081`):
  - `LeaderLock` runs `ILeaderService`s per leadership term; a lost session demotes to standby.
  - `SymbolChangeListener` and `SymbolSubscriptionManager`: idempotent reconcile, ignoring `op`.
  - `BinanceStreamCollector` → `CandlePipe` (10k, `DropOldest`) → `CandleWriter`, which sends no NOTIFY.
  - `BackfillScheduler`/`GapBackfiller` (tail + 48h scan).
  - Readiness: freshness is read from the DB.
  - OTel metrics.

**Decisions and deviations:**
- **Freshness comes from the DB** (`GetLatestOpenTimesAsync`), not an in-memory `lastCandleAt`, so backfilled candles count.
- **Retries are hand-rolled with exponential backoff, not Polly** (no new package).
- **Options class is named `BinanceEndpointOptions`** (`BinanceOptions` clashes with Binance.Net).
- **The 24h backfill cap applies only to symbols with no data or newly (re)subscribed ones.** Startup and reconnect tails catch up fully.
- **Not done:** ADR-005's "readiness fails if `gap_minutes > 5` for more than 10 min". The gauge exists; readiness uses freshness only.
- **Candles still in the pipe at shutdown aren't flushed.** The next leader's tail and scan recover them.

**Validation:** 30 new tests (WebApplicationFactory + Testcontainers + fakes), with the Ingestion project run 3 more times without failures. A **live Binance run**:
- Exactly 3 rows per minute, with about 0.5s ingest lag.
- After a **5-minute kill and restart**, 5 candles per symbol were backfilled and the gap-scan query returned 0 missing minutes.
- Runtime add/remove of SOLUSDT through `pg_notify` worked, including a 1440-candle backfill.
- **Failover:** the standby took the lock 8s after the leader was killed, and there were no gaps.

### P5: HTTP API (2026-10-18, parallel session, branch `claude/p5-api-8f5975`)

Full log: [docs/phase-notes/P5.md](docs/phase-notes/P5.md).

**What was built:**
- **MVC + DataContract XML**, with `XmlProblemDetailsWriter` serializing MVC's ProblemDetails wrappers as `application/problem+xml` for errors raised outside MVC.
- **API-key auth** (constant-time compare; every admin request is rejected when no key is configured) and a UTC binder for `s`.
- **`PricesController`** and **`SymbolsController`**:
  - POST checks Binance exchangeInfo (the full TRADING set, cached 1h; 503 when Binance is unreachable).
  - `SymbolAdminService` writes and notifies in one transaction, only when something changed.
- **`CachingPriceAnalysisService` decorator.** Readiness runs DB + pending-migrations checks. OpenAPI at `/openapi/v1.json`, plus OTel.

**Decisions and deviations:**
- **No redundant probe:** the decorator runs an inner `PriceAnalysisService` over a per-request read snapshot that memoizes the symbol lookup and `GetLatestOpenTimeAsync`. A hit costs symbol lookup + probe; a miss adds one analysis query. The cached value is computed from exactly the `latestOpenTime` in its key.
- **The SMA cache key uses `referenceTime`, not raw `s`**, so every `s` in the same bucket shares an entry.
- **Settled windows (more than 48h old) leave `latestOpenTime` out of the key**, so the 1h sliding rule can take effect. Accepted edge case: a worker down for more than 48h backfills older data, and a settled entry can stay stale until it goes 1h without a hit.
- **A dedicated, size-limited `MemoryCache`** (200k units).
- **`NotAcceptableFilter` returns 406 before the action runs.** `ReturnHttpNotAcceptable` alone would 406 only *after* a POST had committed and notified, and never for 204s.
- **503 for an exchangeInfo outage** (not in the spec table). The POST body is the stored `SymbolDto`, with a `Location` header.
- **The cache metric sits on a second `Meter` instance with the same name**, so P4's metric file wasn't touched.

**Validation:** 65 new tests.
- JSON, XML and 406 on every endpoint, including POST/DELETE.
- XML/JSON 400, 401 and 404, and unmatched routes.
- `symbol_changes` checked with a real `LISTEN`, including a rollback (500 and no notification) and the 409/400/503 paths.
- Cache freshness after a new candle; a repair behind `latest` served from cache until a newer candle arrives; `s` in one bucket shares an entry; settled windows survive new candles.
- Readiness returns 503 with pending migrations.
- Not run: live exchangeInfo, which belongs to P9.

### P4 + P5 merge (2026-10-18)

- Both branches were merged into `main` with `--no-ff` (`0291a11`, `bee6116`). They touched no common files, so there were no conflicts.
- Both apps read the same `MarketData:Binance:RestBaseUrl` key. Each process registers only the Binance services it needs (the worker: market data; the API: the exchangeInfo validator), so no shared helper is needed.
- `tests/Directory.Build.props` no longer passes `--ignore-exit-code 8`, since every test project now has tests.
- **Validation on merged `main`:**
  - `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
  - `dotnet test --solution MarketData.slnx`: **233 passed** (97 Core + 41 Infrastructure + 65 API + 30 Ingestion), exit 0.
  - `dotnet ef migrations has-pending-model-changes`: no changes.

### P6: Console client (2026-10-18)

**What was built** (`src/MarketData.Client`, assembly `marketdata`):
- `MarketDataApiClient`, a typed client over `HttpClient`:
  - It sets `Accept` from `--format json|xml` and deserializes the shared Contracts DTOs with System.Text.Json (web defaults) or `DataContractSerializer`.
  - POST bodies are sent in the same format.
  - It also returns the raw body for `--raw`.
- `ProblemReader` reads RFC 7807 errors in both formats: JSON `title`/`detail`/`errors`, and MVC's XML `urn:ietf:rfc:7807` `title`/`detail`/`MVC-Errors`. Anything else falls back to the status line. Errors surface as `ApiProblemException`.
- `Cli` (System.CommandLine 2.0):
  - `avg <symbol>`
  - `sma <symbol> -n -p [-s] [--points] [--series [--count]]`
  - `symbols list|add|remove`
  - Recursive options: `--url` (`MARKETDATA_URL`, default `http://localhost:8080`), `--format`, `--raw`, `--api-key` (`MARKETDATA_ADMIN_KEY`).
  - Output: aligned tables and key/value lines with UTC times and prices to at most 10 decimals (trailing zeros trimmed). Errors, including validation messages per parameter, go to stderr.
- `Cli.RunAsync(args, out, err, createHttpClient)` takes injectable writers and an HttpClient factory, so tests can drive it against the in-memory API host.

**Decisions and deviations:**
- **Exit codes:** 0 success, 1 API error (ProblemDetails), 2 API unreachable, timed out or unreadable. System.CommandLine's own non-zero code for usage errors.
- **Added `--raw` and `--api-key`** (not in the plan). `--raw` prints the negotiated body as received, which makes JSON vs XML visible. `--api-key` is an explicit alternative to the env variable.
- **`--url` needs a custom parser**, because System.CommandLine 2.0 has no built-in `Uri` conversion (the first test run failed with "Cannot parse argument … as expected type System.Uri"). It accepts only absolute http(s) URLs.
- **`--points` maps to `includePoints=true`; the client otherwise sends `includePoints=false`** to keep the default output short. The API default (`true`) is unchanged.
- **The client passes `count` without `--series` through to the server**, so that misuse gets the same 400 as any other caller.
- **`-s` is URL-escaped**, so `+02:00` offsets survive.
- **The client tests live in `tests/MarketData.Api.IntegrationTests`** (`ConsoleClientTests`, with a project reference to the client) to reuse the API fixture's Postgres container and host rather than adding another test project.

**Validation:**
- `dotnet build MarketData.slnx`: 0 warnings, 0 errors.
- `dotnet test --solution MarketData.slnx`: **251 passed** (18 new `ConsoleClientTests`). They cover, in both formats:
  - avg, SMA with points, and the SMA series table;
  - validation errors on stderr with per-parameter keys; 404 unknown symbol;
  - symbols list/add/409/remove/reactivate.
  - They also cover a `-s` offset with `+`, 401 with a key hint, `--raw` JSON/XML bodies, an unreachable API (exit 2), a missing `-n` and an invalid `--url` (usage errors).
- **Live smoke run** (local, not Docker): `postgres:17-alpine` on :55433, `dotnet ef database update`, then the worker and the API run in Development against live Binance.
  - The worker backfilled 4320 candles in about 8s and was leader-ready.
  - Against the API, `marketdata` returned the 24h average (JSON and XML), `SMA(20, 5m)`, `SMA(3, 30m) --points --format xml`, and a 3-value series.
  - `-p 1h` returned a 400 with the `p` message; `symbols list --format xml` worked.
  - `symbols add SOLUSDT` was accepted by **live exchangeInfo**. The worker logged the notification and subscribed, and SOLUSDT had 1441 candles shortly after. `symbols add NOTAREALPAIR` was rejected with a 400.
  - `--raw --format xml` printed the `<avgPrice>` document.
  - Processes and the container were removed afterwards.

### P7: Containerization (2026-10-18)

**What was built:**
- **`Dockerfile`**, one multi-stage build:
  - The `sdk:10.0` build stage does a locked restore from project files only (cached layer), then copies `src/` and publishes per target.
  - Four targets:
    - `api` and `ingestion` (`aspnet:10.0`);
    - `migrator` (`runtime-deps:10.0` + a self-contained `efbundle` for `linux-x64`/`linux-arm64`, chosen from `TARGETARCH`);
    - `client` (`runtime:10.0`).
  - Every image runs as the non-root `app` user (UID 1654). api and ingestion have a `HEALTHCHECK` on `/health/ready`.
- **`.dockerignore`** keeps tests, docs, `.git`, `.claude`, `bin/obj` and `.env` out of the build context.
- **`docker-compose.yml`:**
  - Startup order: `postgres:17-alpine` (volume, `pg_isready`) → `migrator` (`restart: "no"`) → `ingestion` + `api` (`service_completed_successfully`). `client` sits under `profiles: [tools]`.
  - One connection string, defined once as a YAML anchor.
  - Every setting comes from `.env` with a local default. `.env.example` is committed; `.env` was already gitignored.

**Decisions and deviations:**
- **Healthchecks use bash `/dev/tcp`, not curl.** The .NET 10 runtime images are Ubuntu 24.04 with bash but no curl or wget. Installing curl would add packages and attack surface to every image, so the probe sends a raw `GET /health/ready` and checks for ` 200 `.
- **`GSS Encryption Mode=Disable` in the compose connection string.** This avoids the `libgssapi_krb5.so.2` warning seen in P2. Container logs had 0 GSSAPI mentions.
- **The worker's health port 8081 isn't published.** Only the API (`${API_PORT:-8080}`) and Postgres (`${POSTGRES_PORT:-5432}`, for local tools) are.
- **`ADMIN_API_KEY` defaults to `dev-admin-key`**, so `docker compose up` works without a `.env`. `.env.example` and the README (P8) say to change it.
- **Skipped the optional `roles.sql`:** compose uses one Postgres user. Separate migrator/app roles are a production concern, to be documented in P8.
- **Image sizes** as reported by Docker Desktop's containerd store: api 380 MB, ingestion 379 MB, migrator 347 MB (self-contained bundle), client 301 MB. No trimming was attempted.

**Validation** (Docker Desktop 29.8.1; host ports overridden to `POSTGRES_PORT=55434`, `API_PORT=18090` to avoid local clashes):
- `docker compose config --quiet` passes, and `docker compose --profile tools build` builds all 4 images.
- **`docker compose up -d`:**
  - `migrator` applied `20261018160400_Initial` under EF's migration lock and exited 0. Only then did `ingestion` and `api` start.
  - Both were `healthy` about 6s later, and the worker had backfilled 4320 candles (3 × 24h).
- **Client, curl and migrator re-run:**
  - `docker compose run --rm client`: `avg BTCUSDT` (full 24h), `sma ETHUSDT -n 10 -p 30m --format xml`, and `symbols list` (3 active symbols with recent `lastCandleAt`).
  - From the host: curl with `Accept: application/xml` returned the `<avgPrice>` document, and `Accept: text/csv` returned 406.
  - A `docker compose run --rm migrator` re-run printed "No migrations were applied. The database is already up to date."
- **Standby and failover:**
  - `--scale ingestion=2`: `ingestion-2` was healthy with role `standby` while `ingestion-1` was `leader`.
  - After `docker stop` on the leader, `ingestion-2` became leader within 2s. One readiness probe during takeover was "listener not connected", then it went healthy.
  - Each symbol then had contiguous candles up to the current minute.
- Torn down with `docker compose --profile tools down -v`.
