# Implementation Plan: Market Data Collection & Analysis Service

Inputs: [requirements.md](requirements.md), [system-design.md](system-design.md), and the accepted ADRs in [adr/](adr/README.md) (001, 002, 004–008; 003 is superseded). This plan only orders the work. When it seems to disagree with the design, the design and ADRs win.

---

## 1. Delivery overview

```
P0 Scaffold ──► P1 Core + Contracts ──► P2 Persistence ──► P3 Pg notifications ──┬──► P4 Ingestion worker ──┐
                        │                                                       └──► P5 API ──────────────┼──► P7 Containers ──► P9 Validation
                        └──────────────────────────────► P6 Console client (against Contracts) ──────────┘
P8 Docs & deliverables: started in P0, updated at the end of every phase
```

| Phase | Outcome | Est. | Depends on |
|---|---|---|---|
| P0 | Solution builds; repo conventions; notes started | 0.5 d | — |
| P1 | Domain math + DTOs, fully unit-tested | 1 d | P0 |
| P2 | Schema, migrations, bundle, repositories (Postgres-tested) | 1.5 d | P1 |
| P3 | Reusable LISTEN/NOTIFY publisher + listener | 0.5 d | P2 |
| P4 | Ingestion worker end to end (WS → DB, backfill, leadership) | 2 d | P3 |
| P5 | HTTP API (analysis, admin, negotiation, cache, auth) | 2 d | P3 |
| P6 | Console client | 0.5 d | P1 (contract); P5 for live testing |
| P7 | Dockerfile targets + compose | 0.5 d | P4, P5 |
| P8 | README, engineering notes, CI | 0.5 d (+ ongoing) | — |
| P9 | E2E + manual acceptance against live Binance | 0.5 d | P7 |
| **Total** | | **≈ 9.5 dev-days** | |

**Parallelism:** once P3 is done, **P4 and P5 can run in parallel** (separate projects; they share only Infrastructure, which is frozen by then). P6 can start right after P1 against the DTOs.

---

## 2. Phases

Each phase ends with its **Definition of Done (DoD)**: `dotnet build` with zero warnings, all tests green, and `engineering-notes.md` updated.

### P0: Scaffold & conventions
- [x] `git init`, `.gitignore` (VisualStudio template), `.gitattributes`, `.editorconfig`.
- [x] `MarketData.slnx` with `src/` projects: `Contracts`, `Core`, `Infrastructure`, `Api` (web), `Ingestion` (worker), `Client` (console). `tests/` projects: `Core.Tests`, `Api.IntegrationTests`, `Ingestion.IntegrationTests`.
- [x] `Directory.Build.props`: `net10.0`, `Nullable=enable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`.
- [x] `Directory.Packages.props` (central package management). Initial packages, at the latest stable versions when scaffolding:
  - Runtime: `Binance.Net`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.Extensions.Http.Resilience`, `OpenTelemetry.Extensions.Hosting` + exporters, `System.CommandLine`.
  - Tests: `xunit.v3` (on Microsoft.Testing.Platform), `Testcontainers.PostgreSql`, `Microsoft.AspNetCore.Mvc.Testing`, `NSubstitute`.
- [x] `.config/dotnet-tools.json` pinning `dotnet-ef`.
- [x] Project references: Core → Contracts; Infrastructure → Core; Api/Ingestion → Infrastructure; Client → Contracts only.
- [x] Start `engineering-notes.md` (approach, links to ADRs).

**DoD:** `dotnet build` and `dotnet test` (empty) pass. The reference graph matches the above.

### P1: Core domain + Contracts
- [x] `TimePeriod` value object: parse `1m|5m|30m|1d|1w` (case-sensitive, so `1M` is never read as a minute), `ToTimeSpan()`, `Floor(DateTimeOffset)` with the Monday week origin `2000-01-03Z`.
- [x] `SymbolName` normalization (trim, upper-case, `^[A-Z0-9]{5,20}$`).
- [x] `SmaCalculator`: single value and rolling series (O(n + count) sliding `decimal` sum, partial windows, elements with zero points omitted).
- [x] `PriceAnalysisService` orchestration against interfaces: `ICandleQueryRepository` (including `GetLatestOpenTimeAsync`, added in P3), `ISymbolRepository`. It handles the anchoring rule `effectiveRef = floor(min(s ?? now, latest), p)` and the window `[latest − 24h, latest)`.
- [x] Request validation: `1 ≤ n ≤ 1000`; `count` 1..1000 and only with `series=true`; `s` not in the future; default `count = min(n, 100)`.
- [x] Contracts DTOs with `[DataContract]`/`[DataMember(Name=camelCase, EmitDefaultValue=false)]`: `AvgPriceResponse`, `SmaResponse` (+ `SmaPoint`, `SmaSeriesItem`), `SymbolDto`, `AddSymbolRequest`.
- [x] Unit tests (ADR-006 list): boundary and off-boundary `s`, `s > latest`, week boundary, gaps, series vs. single equivalence, validation errors.

**DoD:** 100% branch coverage on `TimePeriod` and `SmaCalculator`. DTOs round-trip through both the JSON and the DataContract XML serializers.

### P2: Persistence (ADR-002, ADR-008)
- [x] `MarketDataDbContext` (snake_case naming), entities `Symbol` and `Candle`. Composite PK `(symbol_id, open_time)`, `numeric(28,10)`/`numeric(38,10)`.
- [x] `IDesignTimeDbContextFactory` (connection from env `ConnectionStrings__MarketData`, falling back to a localhost default).
- [x] Migration `Initial` with `HasData` for `BTCUSDT`, `ADAUSDT`, `ETHUSDT`.
- [x] Repositories (Infrastructure):
  - `UpsertCandlesAsync(batch)`: a single `INSERT … ON CONFLICT DO UPDATE` (multi-row VALUES or `unnest` arrays).
  - `GetBucketClosesAsync(symbolId, step, from, to, limit)`: the `date_bin` + `DISTINCT ON` query.
  - `Get24hAverageAsync(symbolId, latest)`: `avg(close)`, `count`, `min(open_time)`.
  - `GetLatestOpenTimesAsync()`: `max(open_time)` per symbol.
  - `FindMissingMinutesAsync(symbolId, window)`: the `generate_series` gap scan, merged into ranges.
  - Symbol CRUD (soft delete / reactivate).
- [x] `PendingMigrationsHealthCheck` (shared, used by both hosts).
- [x] Bundle build verified locally: `dotnet ef migrations bundle … --self-contained -r linux-x64`.
- [x] Integration tests in a new `tests/MarketData.Infrastructure.IntegrationTests` project (Testcontainers Postgres, fixture runs `MigrateAsync`, which is allowed in tests): upsert is idempotent, rollup at minute, day and week boundaries, gap scan, `HasData` seed present.

**DoD:** `dotnet ef migrations has-pending-model-changes` is clean. The bundle applies to an empty DB and a re-run is a no-op.

### P3: Postgres notifications (ADR-007)
- [x] `IPgNotificationPublisher.NotifyAsync(channel, payload, transaction?)` → `SELECT pg_notify(@c, @p)` (enlists in the caller's EF transaction).
- [x] `PgNotificationListener` (abstract `BackgroundService`): dedicated non-pooled connection (`Pooling=false; KeepAlive=30`). Connect → `LISTEN` → `OnConnectedAsync` (reconcile hook) → `WaitAsync` loop. Exponential reconnect with jitter. Metrics `pg_listener_reconnects_total`, `notifications_received_total`.
- [x] Payload record + `System.Text.Json` source-gen context: `SymbolChangedPayload`. (`CandlesWrittenPayload` was dropped in P3; see ADR-007 amendment.)
- [x] Tests: notify inside a rolled-back transaction delivers nothing, notify on commit is delivered, after a forced `pg_terminate_backend` the listener reconnects and calls the reconcile hook.

**DoD:** the listener survives a backend kill in tests, and reconcile is called on every (re)connect.

### P4: Ingestion worker (ADR-001, ADR-005, ADR-007)
- [x] Binance adapters (use the `.claude/skills/binance-net` skill): `IMarketDataStream` (subscribe or unsubscribe per symbol, `ConnectionLost`/`ConnectionRestored` events) and `IKlineRestClient` (paged klines, ≤1000 per request, honouring `Retry-After`). **Base URLs are configurable** (see Risks).
- [x] `KlineMapper`, shared by the WS and REST paths. Golden-file tests for both inputs give identical `Candle`s.
- [x] `LeaderLock` hosted service: `pg_try_advisory_lock` on a dedicated connection, standby retry every 10s, gates the other services.
- [x] `BinanceStreamCollector` → bounded `Channel<Candle>` (10k, `DropOldest`, drop counter) → `CandleWriter` (batch ≤500 rows or 1s, Polly retry; no notification after commit).
- [x] `GapBackfiller`: tail mode (startup, `ConnectionRestored`, symbol add capped at `BackfillOnAdd`=24h) and scan mode (every 5 min, plus on drops or DB recovery).
- [x] `SymbolChangeListener` (P3 base): on connect, reconcile active symbols against live subscriptions; on notification, subscribe or unsubscribe idempotently; 60s periodic reconcile.
- [x] Health on `:8081`: live; ready = DB + no pending migrations + role. For the leader: WS connected, listener connected, and every active symbol fresh within 2 min.
- [x] OpenTelemetry metrics per system-design §7.
- [x] Integration tests (worker host + Testcontainers + `FakeMarketDataStream` + stubbed `IKlineRestClient`): candles persisted (no notification, per the P3 decision); mid-series hole repaired by the scan; `symbol_changes` add/remove changes subscriptions; second worker stays standby, then takes over when the leader's connection drops.

*Done 2026-10-18; see `docs/phase-notes/P4.md`. Open item: readiness on `gap_minutes` (ADR-005 action 3) is not wired.*

**DoD:** a manual run against live Binance writes one row per symbol per minute. Killing the worker for 5 minutes and restarting it leaves no gaps.

### P5: HTTP API (ADR-004, ADR-006, ADR-007)
- [x] MVC + `AddXmlDataContractSerializerFormatters`, `RespectBrowserAcceptHeader`, `ReturnHttpNotAcceptable`. `AddProblemDetails` + `XmlProblemDetailsWriter` registered before the JSON writer.
- [x] `ApiKeyAuthenticationHandler` (`X-Api-Key`, `Admin:ApiKey`) + `Admin` policy.
- [x] `PricesController`: `GET /api/{symbol}/24hAvgPrice` and `GET /api/{symbol}/SimpleMovingAverage?n&p&s&series&count&includePoints`.
- [x] `SymbolsController` (Admin):
  - `GET` with `lastCandleAt`.
  - `POST` validates through `exchangeInfo` (cached 1h), then writes and notifies in one transaction; 201/200/400/409.
  - `DELETE` soft-deletes; 204/404.
- [x] Every analysis request reads `latestOpenTime` through `ICandleQueryRepository.GetLatestOpenTimeAsync` (one index probe). No listener and no in-memory state in the API.
- [x] Caching: `IMemoryCache` key `(endpoint, symbol, params, latestOpenTime)`, 60s absolute expiry. Results whose window ends more than 48h before `latest` cached for 1h (sliding).
- [x] Health: DB + no pending migrations.
- [x] Integration tests (`WebApplicationFactory`):
  - Every endpoint returns JSON, XML, and 406 for `text/csv`.
  - 400, 401 and 404 as XML ProblemDetails.
  - Admin writes emit `symbol_changes`; a rollback emits nothing.
  - A newly stored candle changes the cache key (no stale result once the next minute is stored).
  - Readiness is red when migrations are pending.

*Done 2026-10-18; see `docs/phase-notes/P5.md`.*

**DoD:** the full test matrix is green. OpenAPI document generated (`Microsoft.AspNetCore.OpenApi`) for README examples.

### P6: Console client
- [x] System.CommandLine commands:
  - `avg <symbol>`
  - `sma <symbol> -n -p [-s] [--points] [--series [--count]]`
  - `symbols list|add|remove`
- [x] Global options: `--url` (env `MARKETDATA_URL`) and `--format json|xml`, which sets `Accept`. Admin key comes from `MARKETDATA_ADMIN_KEY`.
- [x] Typed `HttpClient` deserializes JSON or XML into the shared DTOs, prints tables (with `from`/`to`/`referenceTime` shown), maps ProblemDetails to stderr, and exits non-zero on errors.
- [x] Tests against the in-memory API host (`WebApplicationFactory` handler), covering both formats.

*Done 2026-10-18; see `engineering-notes.md` § P6.*

**DoD:** every analysis endpoint can be called from the client in both formats.

### P7: Containerization (ADR-008)
- [x] One multi-stage `Dockerfile` with targets `api`, `ingestion`, `migrator` (`runtime-deps` + `efbundle`) and `client`. Non-root user. `HEALTHCHECK` for api and ingestion.
- [x] `docker-compose.yml`: `postgres:17` (volume, `pg_isready`) → `migrator` (`restart: "no"`) → `api` + `ingestion` (`service_completed_successfully`). `client` sits under `profiles: [tools]`. Config through `.env` (`.env.example` committed).
- [ ] *(Skipped in P7: compose uses a single user; separate roles are documented for production in P8.)* Optional: `postgres/init/roles.sql` creating `marketdata_migrator` and `marketdata_app`. Compose defaults to a single user (documented).

*Done 2026-10-18; see `engineering-notes.md` § P7.*

**DoD:** `docker compose up --build` on a clean machine gives a healthy API and worker and 3 symbols ingesting. `docker compose run --rm client avg BTCUSDT` works after about 2 minutes.

### P8: Documentation & deliverables (ongoing)
- [x] `README.md`: prerequisites, quick start, configuration reference (all `MarketData:*`, `Admin:*`, connection strings), **DB initialization** (compose migrator, local `dotnet ef database update`, k8s init container, roles, expand/contract rule), API reference with curl examples for JSON and XML, the analysis semantics (A1/A2 + anchoring), client usage, troubleshooting (Binance geo-restrictions, stale data).
- [x] `engineering-notes.md`: approach, key decisions (link ADRs), deviations, and **validation steps actually run**, with results.
- [x] CI (GitHub Actions): restore with locked packages, build, test (Testcontainers), `has-pending-model-changes`, build all Docker targets, bundle smoke-run against a service container.

### P9: Validation & acceptance
| Requirement | Check |
|---|---|
| F1/F2 collect & store | After 10 min of compose: 10 rows/symbol, `ingest_lag_seconds` p95 < 5s |
| F3 runtime symbols | `client symbols add SOLUSDT` → rows within 2 min + 24h backfill; `remove` → ingestion stops, history still queryable |
| F4 24h avg | Value matches `avg(close)` computed by hand in SQL; `isFullPeriod=false` on a fresh DB |
| F5 SMA | Compare `n=20,p=5m` against a Binance 5m kline closes calculation; series last element = single value; `s` on and off boundary |
| F6 JSON/XML | curl with `Accept: application/json`, `application/xml`, `text/csv` (406), plus an error in XML |
| F7 client | All commands, both formats |
| Resilience | Kill the worker 5 min, or stop Postgres 2 min → no gaps after recovery (gap-scan query returns 0) |
| Deployment | Clean-clone `docker compose up --build` succeeds; `migrator` exits 0 and a re-run is a no-op |

Record the outcomes in `engineering-notes.md`.

*Done 2026-10-18: all rows pass; see `engineering-notes.md` § P9 (open finding: history depth for `1d`/`1w`).*

---

## 3. Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Binance blocks `stream.binance.com`/`api.binance.com` in some regions (e.g. HTTP 451) | No data | Make WS/REST base URLs configurable. Document the market-data-only endpoints (`data-stream.binance.vision`, `data-api.binance.vision`). Check reachability in P0 |
| .NET 10 / EF Core 10 provider or Binance.Net compatibility | Build blockers | Pin versions in P0. Run a smoke compile against Npgsql EF 10 and the Binance.Net version first |
| DataContract XML quirks (ProblemDetails extensions, `DateTimeOffset`, nullable omission) | F6 failures | Golden XML tests in P1/P5. XML ProblemDetails writer tested separately |
| LISTEN/NOTIFY timing makes tests flaky | Slow CI | Await notifications with a `TaskCompletionSource` and a timeout. Never use fixed sleeps |
| Testcontainers on Windows / Docker Desktop | Local test friction | One shared Postgres container per test assembly (collection fixture); document Docker requirement |
| `date_bin` worst-case query (n+count=2000, 1w) | Latency SLO | Benchmark in P2. If p95 > 500 ms, fall back to rollup tables (ADR-002 revisit) |

---

## 4. Proposed next steps

1. **Confirm the choices below**, then start **P0** (scaffold, git init, conventions).
2. **Check Binance reachability early** from your network (one `curl https://api.binance.com/api/v3/ping`). It decides whether the alternative market-data endpoints are needed as defaults.
3. **Run P1 → P3 in sequence in one session.** They define the shared foundations.
4. **Then split P4 and P5 into two parallel sessions** (separate worktrees and branches), with P6 alongside, and merge before P7.
5. **P7 → P9 together,** finishing with the acceptance table and final docs.

**Choices to confirm (defaults in bold):**
- Test stack: **xUnit v3 + NSubstitute + plain asserts** (FluentAssertions v8+ needs a commercial licence).
- CI: **GitHub Actions** (or none for the assignment).
- Git hosting: init locally now, and add a remote when you decide.
