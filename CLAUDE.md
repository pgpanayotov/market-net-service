# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

There is no code yet. Implementation is meant to follow these documents:
- `docs/requirements.md`: the assignment spec (.NET 10 service that collects Binance Spot prices and offers analysis endpoints, plus a console client).
- `docs/system-design.md`: the full design (data model, API contracts, pipelines, deployment).
- `docs/adr/`: accepted decisions (ADR-001, 002, 004–008; ADR-003 is superseded). Don't contradict an accepted ADR. If a change is needed, write a new ADR that supersedes it and update `docs/system-design.md` and the index in `docs/adr/README.md`.

The project skill `.claude/skills/binance-net` covers idiomatic Binance.Net usage (WebSocket subscriptions, REST klines/exchangeInfo, result handling). Use it for any Binance code.

Required deliverables beyond code: `README.md` (setup, configuration, DB initialization) and `engineering-notes.md` (approach, key decisions, validation steps).

## Commands

Tests run on **Microsoft.Testing.Platform** (set in `global.json`; xUnit v3 doesn't support VSTest on the .NET 10 SDK), so use the `--solution`/`--project` forms and xUnit's own filter flags. The build treats warnings as errors and uses NuGet lock files (`packages.lock.json`; commit them after any package change).

```bash
dotnet tool restore
dotnet build MarketData.slnx
dotnet test --solution MarketData.slnx
dotnet test --project tests/MarketData.Core.Tests --filter-class "MarketData.Core.Tests.TimePeriodTests"   # one class
dotnet test --project tests/MarketData.Core.Tests --filter-method "*Floor*"                                 # matching methods
```

The commands below are planned (the projects or files they need come in later phases):
```bash
docker compose up --build                                                         # postgres → migrator → ingestion + api (:8080)
docker compose run --rm migrator                                                  # apply migrations only
docker compose run --rm client avg BTCUSDT                                        # console client
dotnet ef migrations add <Name> --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure
dotnet ef database update --project src/MarketData.Infrastructure                 # local DB without Docker
dotnet ef migrations has-pending-model-changes --project src/MarketData.Infrastructure
```
`dotnet-ef` is pinned in `.config/dotnet-tools.json`. Integration tests use Testcontainers, so Docker must be running. Package versions live only in `Directory.Packages.props` (central package management), so `<PackageReference>` entries carry no `Version`.

Record each phase's decisions, deviations and validation results in `engineering-notes.md` (a required deliverable).

## Architecture (big picture)

Three runtime processes share one PostgreSQL database:

- **`MarketData.Ingestion`** (Worker Service; one active instance, guarded by `pg_try_advisory_lock`; others wait on standby). It subscribes to Binance `<symbol>@kline_1m` through the combined-stream WebSocket and persists only **closed** 1-minute candles to `candles_1m`.
  - Pipeline: collector → `Channel<Candle>` → batched upsert `CandleWriter`.
  - `GapBackfiller` makes the store converge to complete. It does tail backfill from REST `/api/v3/klines`, plus a 48h `generate_series` gap scan. Lossy in-memory paths are fine because of this, so don't add a durable queue.
- **`MarketData.Api`** (stateless, horizontally scalable, MVC controllers). Analysis endpoints and admin symbol endpoints. Never opens Binance WebSockets.
- **`migrator`**: an EF Core migration bundle (`efbundle`) run as an init container. **Apps never call `MigrateAsync`/`EnsureCreated` or seed at startup** (only test fixtures may). They only check for pending migrations and report not-ready if any are found. Initial symbols are seeded with `HasData` in the initial migration.

Cross-process signals use **Postgres LISTEN/NOTIFY**, treated as hints. The DB is the source of truth.
- `symbol_changes` (API → worker) is sent with `pg_notify` **in the same transaction** as the `symbols` write. Admin POST/DELETE are therefore asynchronous with respect to ingestion.
- `candles_written` (worker → every API replica) is sent after each committed batch. It updates the API's per-symbol `latest` timestamp and fires that symbol's `IMemoryCache` change token.
- Every listener uses a dedicated non-pooled connection and follows **LISTEN first, then reconcile from the DB** on each (re)connect. The worker also reconciles every 60s. Handlers must be idempotent.

Projects: `Contracts` (DTOs shared with the client, with `[DataContract]` attributes), `Core` (domain logic such as `TimePeriod` and the analysis service, no I/O), `Infrastructure` (EF Core DbContext + migrations + design-time factory, Binance adapters, raw SQL rollups, Pg listener), `Api`, `Ingestion`, `Client` (System.CommandLine).

## Invariants that are easy to break

- **Content negotiation applies to every response, errors included.** Use controllers with XML DataContract formatters (Minimal APIs can't return XML). Errors raised outside MVC go through a custom XML `IProblemDetailsWriter`. Admin auth is an API-key `AuthenticationHandler` (`X-Api-Key`) with the `Admin` policy.
- **Analysis is anchored at the newest stored data, not the wall clock:** `latest = max(open_time) + 1m`.
  - 24h average uses `[latest − 24h, latest)`.
  - SMA uses `referenceTime = floor(min(s ?? now, latest), p)` and counts complete buckets only. A bucket's value is the close of its last 1m candle.
  - Weeks start Monday 00:00 UTC (`date_bin` origin `2000-01-03Z`).
- SMA `?series=true&count=m` returns a rolling series (default `count = min(n, 100)`; `n` and `count` ≤ 1000; `count` without `series=true` → 400). It uses one query for `n + count − 1` buckets and a sliding `decimal` sum.
- Prices are `numeric` in the DB and `decimal` in C#, never `double`. Candle writes are idempotent upserts on PK `(symbol_id, open_time)`. WS and REST paths share one kline mapper.
- Removing a symbol is a soft delete (`is_active = false`). History is never purged and stays queryable.
- Migrations must be backward compatible with the running release (expand/contract), because the init container runs while old pods still serve.
- `LISTEN` connections need session-mode pooling, so don't route them through PgBouncer in transaction mode.
