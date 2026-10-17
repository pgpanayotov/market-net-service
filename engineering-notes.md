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
