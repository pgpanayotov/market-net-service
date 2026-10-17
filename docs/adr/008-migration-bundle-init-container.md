# ADR-008: Apply schema with an EF Core migration bundle run as an init container

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
The first draft applied migrations at app startup (`Database:MigrateOnStartup` → `Database.MigrateAsync()`). With two hosts (ADR-007) and a horizontally scaled API, that means several processes racing to migrate on every start. Every app would also need DDL rights, and a slow or failed migration would be mixed up with app startup. The owner decided: **no `MigrateOnStartup`; use an EF migration bundle run as an init container.**

Initial symbol seeding also ran at app startup and has to move for the same reason.

## Decision
- **No app process performs schema or seed writes at startup.** `MigrateAsync()`, `EnsureCreated()` and startup seeding are removed from `MarketData.Api` and `MarketData.Ingestion`.
- **Bundle build:** a `migrator` stage in the shared multi-stage Dockerfile runs
  `dotnet ef migrations bundle --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure --self-contained -r linux-x64 -o /out/efbundle`.
  `MarketData.Infrastructure` provides an `IDesignTimeDbContextFactory<MarketDataDbContext>`, so the bundle doesn't depend on either app's `Program`. `dotnet-ef` is pinned in `.config/dotnet-tools.json`.
- **Bundle image:** `mcr.microsoft.com/dotnet/runtime-deps:10.0` + `efbundle`. Entrypoint `./efbundle --connection "$ConnectionStrings__MarketData"`. It exits 0 when the schema is current, so reruns are idempotent. EF Core (9+) takes a migration lock, so overlapping runs are serialized.
- **Init container / ordering:**
  - **docker-compose:** service `migrator` (`restart: "no"`, `depends_on: postgres: service_healthy`). `api` and `ingestion` use `depends_on: migrator: condition: service_completed_successfully`.
  - **Kubernetes:** `initContainers: [migrator]` on both Deployments (or a pre-upgrade Job), using a migration-role secret.
- **Seeding:** the three initial symbols (`BTCUSDT`, `ADAUSDT`, `ETHUSDT`) are **reference data in the initial migration** (`HasData`), so the bundle seeds them. After that, symbols are managed only through the admin API. The `MarketData:InitialSymbols` config setting is dropped.
- **Startup schema guard:** both apps check at startup (read-only) that the database has no pending migrations for their assembly (`GetPendingMigrationsAsync()`). If any are pending, readiness fails with a clear log message ("schema behind: run migrator") rather than failing later with SQL errors.
- **Least privilege:** the migrator connects as `marketdata_migrator` (DDL owner). The API and worker use `marketdata_app` (DML on `symbols`/`candles_1m`, `LISTEN`/`NOTIFY`). Compose uses one superuser for simplicity; production uses separate roles, documented in the README.
- **Rolling-deploy rule:** because the init container runs while old pods are still serving, migrations must be **backward compatible** with the previous release (expand/contract: add nullable column → deploy → backfill → tighten later).
- **Dev and test workflows:** locally without Docker, `dotnet ef database update` or `docker compose run --rm migrator`. Integration tests apply migrations in their fixture (`MigrateAsync()` in **test code only**) against Testcontainers Postgres.

## Options Considered

### Option A: `MigrateOnStartup` in the apps (previous)
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Safety | Races across replicas/hosts; apps need DDL rights |
| Operability | Migration failures appear as app crash loops |
| Team familiarity | High |

**Pros:** Zero extra artifacts.
**Cons:** All of the above, made worse by the two-host topology.

### Option B: Idempotent SQL script (`dotnet ef migrations script --idempotent`) run by `psql`
| Dimension | Assessment |
|---|---|
| Complexity | Low–Medium |
| Safety | Good; DBA-reviewable |
| Operability | Needs a `psql` image and script plumbing |
| Team familiarity | Medium |

**Pros:** Plain SQL artifact, easy to review.
**Cons:** C# logic in migrations (and `HasData` diffs) must be rendered to SQL. One more tool in the image. Easier to run the wrong script version by mistake.

### Option C: EF migration bundle as init container ✅
| Dimension | Assessment |
|---|---|
| Complexity | Medium (extra Docker stage) |
| Safety | Single runner per deploy, lock-protected, least-privilege roles |
| Operability | Clear failure point before apps start; same artifact in compose and k8s |
| Team familiarity | Medium–High |

**Pros:** A self-contained executable versioned with the code. Idempotent. No SDK or `psql` needed at runtime.
**Cons:** Larger image (~70 MB self-contained). Compose and k8s need ordering configuration.

### Option D: External migration tool (Flyway / Liquibase / DbUp)
**Pros:** Mature, language-agnostic.
**Cons:** Duplicates EF migrations or abandons them. The requirement explicitly names EF Core.

## Trade-off Analysis
C keeps EF Core migrations as the single schema source (the requirement) and moves their execution to one explicit, observable step. B is a good alternative where a DBA must review SQL, and C doesn't rule it out: `migrations script --idempotent` can still be generated for review in CI. Moving seed data into the migration trades configurable defaults for deterministic, startup-free seeding. That's acceptable, because runtime symbol management exists anyway.

## Consequences
- **Easier:** safe scale-out, apps run with DML-only credentials, migration failures are isolated and visible (`docker compose ps` shows `migrator exited (1)`).
- **Harder:** deploys have an extra step. Migration authors must follow expand/contract. Changing the default symbols requires a new migration (or just the admin API).
- **Revisit:** if migrations become long-running (e.g. partitioning `candles_1m`), run them as a separate manual or approved Job instead of an init container so they don't block pod start.

## Action Items
1. [x] `IDesignTimeDbContextFactory` in Infrastructure. Tool manifest pinning `dotnet-ef`.
2. [x] Dockerfile targets `api`, `ingestion`, `migrator`. Compose services with `service_completed_successfully`.
3. [x] Initial migration with `HasData` for the three symbols. Remove `MigrateOnStartup` and `InitialSymbols`.
4. [x] `PendingMigrationsHealthCheck` in both apps' readiness.
5. [ ] README: "Database initialization" section covering compose, local `dotnet ef database update`, k8s init container, roles, and the expand/contract rule.
6. [ ] CI: build the bundle and run it against an ephemeral Postgres. Fail the build on model/migration drift (`dotnet ef migrations has-pending-model-changes`).
