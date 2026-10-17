# MarketData

A .NET 10 service that collects Binance Spot prices over the multi-stream WebSocket, stores closed 1-minute candles in PostgreSQL, and serves price analysis (24h average, simple moving average) over an HTTP API that speaks **JSON and XML**. A console client, `marketdata`, calls the same API.

```
Binance WS ──► ingestion worker ──► PostgreSQL ◄── HTTP API ◄── marketdata CLI / curl
Binance REST ─┘ (backfill)              ▲            │
                         migrator ──────┘            └─ pg_notify(symbol_changes) ──► worker
```

- **Ingestion** (`src/MarketData.Ingestion`) subscribes to `<symbol>@kline_1m` and upserts every closed candle. Gaps are repaired from Binance REST (tail backfill plus a 48h gap scan). Run one worker: there is no leader election, and a brief overlap during a deploy is harmless because writes are idempotent.
- **API** (`src/MarketData.Api`) is stateless. It serves the analysis endpoints and the admin endpoints for symbols. An admin change commits together with a `pg_notify`, which the worker acts on within about a second.
- **Migrator** is an EF Core migration bundle that runs as an init container. The apps never change the schema themselves.

Design and decisions: [docs/system-design.md](docs/system-design.md), [docs/adr/](docs/adr/README.md). How it was built and validated: [engineering-notes.md](engineering-notes.md).

---

## Quick start (Docker)

Prerequisites: Docker with Compose v2, and outbound access to Binance (see [Troubleshooting](#troubleshooting) if Binance is blocked where you are).

```bash
cp .env.example .env              # optional; every value has a default
docker compose up --build -d
```

Compose starts `postgres`, then runs the `migrator` to completion, then starts `ingestion` and `api`. Within a few seconds the worker has backfilled the last 24 hours for `BTCUSDT`, `ETHUSDT` and `ADAUSDT`, and both services report healthy:

```bash
docker compose ps                 # migrator: Exited (0); api, ingestion: healthy
docker compose run --rm client avg BTCUSDT
```

```
BTCUSDT  24h average price
  Average price  81627.1443958333
  Window         2026-10-17 17:31 UTC -> 2026-10-18 17:31 UTC
  Data points    1440 (full 24h)
```

The API is on `http://localhost:8080` (`API_PORT`). The OpenAPI document is at `/openapi/v1.json`.

Other commands:

```bash
docker compose logs -f ingestion
docker compose run --rm migrator            # re-run migrations (a no-op when the schema is current)
docker compose --profile tools down -v      # stop everything and delete the database volume
```

---

## HTTP API

Every endpoint returns JSON or XML according to the `Accept` header (`application/json`, `application/xml`, `text/xml`). Errors use the same negotiation and are RFC 7807 ProblemDetails (`application/problem+json` / `application/problem+xml`). An `Accept` header that allows neither JSON nor XML gets **406**. Symbols are case-insensitive.

| Method | Route | Description |
|---|---|---|
| GET | `/api/{symbol}/24hAvgPrice` | Average price over the last 24 hours of stored data, or over the available period if shorter |
| GET | `/api/{symbol}/SimpleMovingAverage?n=&p=&s=` | Simple moving average of `n` buckets of size `p` (`1m`, `5m`, `30m`, `1d`, `1w`), ending at `s` (optional, default now) |
| GET | `/api/admin/symbols` | List monitored symbols, including removed ones, with `lastCandleAt` *(admin)* |
| POST | `/api/admin/symbols` | Body `{"symbol":"SOLUSDT"}`: start monitoring, or reactivate a removed symbol *(admin)* |
| DELETE | `/api/admin/symbols/{symbol}` | Stop monitoring; the history stays queryable *(admin)* |
| GET | `/health/live`, `/health/ready` | Liveness; readiness (database reachable and schema current) |

Admin endpoints need the header `X-Api-Key: <Admin:ApiKey>`. With compose the key is `ADMIN_API_KEY` (default `dev-admin-key`).

### Examples

```bash
curl -H "Accept: application/json" http://localhost:8080/api/BTCUSDT/24hAvgPrice
```
```json
{"symbol":"BTCUSDT","averagePrice":81627.5480000000,"from":"2026-10-17T17:26:00Z","to":"2026-10-18T17:26:00Z","dataPoints":1440,"isFullPeriod":true}
```

```bash
curl -H "Accept: application/xml" "http://localhost:8080/api/ADAUSDT/SimpleMovingAverage?n=3&p=30m"
```
```xml
<sma xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><symbol>ADAUSDT</symbol><n>3</n><period>30m</period>
  <referenceTime>2026-10-18T17:00:00Z</referenceTime><simpleMovingAverage>0.2160666667</simpleMovingAverage>
  <dataPointsUsed>3</dataPointsUsed><points><point><time>2026-10-18T15:30:00Z</time><close>0.2162</close></point>…</points></sma>
```

```bash
# Rolling series: 3 SMA(5, 1m) values, oldest first
curl "http://localhost:8080/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m&series=true&count=3"

# Explicit reference time (UTC when no offset is given); URL-encode a '+' offset as %2B
curl "http://localhost:8080/api/ETHUSDT/SimpleMovingAverage?n=24&p=1d&s=2026-10-01T00:00:00Z"

# Admin
curl -X POST -H "X-Api-Key: dev-admin-key" -H "Content-Type: application/json" \
     -d '{"symbol":"SOLUSDT"}' http://localhost:8080/api/admin/symbols
curl -X DELETE -H "X-Api-Key: dev-admin-key" http://localhost:8080/api/admin/symbols/SOLUSDT
```

### SMA parameters

| Parameter | Meaning |
|---|---|
| `n` | Number of buckets, 1–1000 |
| `p` | Bucket size: `1m`, `5m`, `30m`, `1d`, `1w` (case-sensitive; `1M` is rejected because it conventionally means a month) |
| `s` | Optional reference time, ISO-8601, not in the future. Without an offset it is read as UTC |
| `series` | `true` returns a rolling series instead of one value |
| `count` | Series length, 1–1000, default `min(n, 100)`. Only valid with `series=true` |
| `includePoints` | `false` omits the bucket closes from a single-value response (default `true`) |

### Status codes

| Status | When |
|---|---|
| 200 / 201 | Success; 201 when a POST created a new symbol, 200 when it reactivated a removed one |
| 204 | DELETE succeeded (also when the symbol was already inactive) |
| 400 | Invalid `n`, `p`, `s`, `count` or symbol name (errors keyed by parameter), or POST of a symbol that isn't a TRADING Binance spot pair |
| 401 | Admin endpoint without a valid `X-Api-Key` |
| 404 | `Unknown symbol` (never configured) or `No data available` (configured, but nothing stored for the window) |
| 406 | `Accept` allows neither JSON nor XML. Checked before anything is written |
| 409 | POST of a symbol that is already active |
| 503 | POST while Binance exchangeInfo is unreachable |

### How the numbers are computed

- **Anchored at the newest stored candle, not the wall clock.** With `latest` = the newest candle's open time + 1 minute:
  - The 24h average covers `[latest − 24h, latest)`.
  - The SMA reference time is `floor(min(s ?? now, latest), p)`.
  - This means a minute whose candle hasn't been stored yet is never half-counted, and a removed symbol's history stays analysable. Every response states its window (`from`/`to` or `referenceTime`).
- **Average price** is the mean of the 1-minute close prices, so each minute counts equally (a time-weighted average).
- **SMA** is the mean of the closes of the last `n` complete `p`-buckets before the reference time.
  - A bucket's close is the close of its last 1-minute candle.
  - Buckets align to UTC; weeks start Monday 00:00 UTC, like Binance's `1w` klines.
  - Missing buckets are skipped, and `dataPointsUsed` reports how many were available.
- **A series** returns exactly `count` values, oldest first, each the SMA(n) ending at its `time`. The last one equals the single-value result. A window without data is still listed, with `dataPointsUsed: 0` and no `simpleMovingAverage` (the console client prints `-`); the response is 404 only when no window has data.

---

## Console client

`marketdata` calls the API and prints tables. Every command takes `--format json|xml` (sets `Accept`) and `--raw` (prints the response body as received).

```bash
docker compose run --rm client avg BTCUSDT
docker compose run --rm client sma ETHUSDT -n 20 -p 5m --points
docker compose run --rm client sma BTCUSDT -n 5 -p 1m --series --count 3 --format xml
docker compose run --rm client sma BTCUSDT -n 24 -p 1d -s 2026-10-01T00:00:00Z
docker compose run --rm client symbols list
docker compose run --rm client symbols add SOLUSDT
docker compose run --rm client symbols remove SOLUSDT
docker compose run --rm client avg BTCUSDT --format xml --raw
```

Without Docker: `dotnet run --project src/MarketData.Client -- <command>`.

| Setting | Option | Environment variable | Default |
|---|---|---|---|
| API base URL | `--url` | `MARKETDATA_URL` | `http://localhost:8080` (`http://api:8080` in the compose image) |
| Admin key | `--api-key` | `MARKETDATA_ADMIN_KEY` | none (compose sets `ADMIN_API_KEY`) |

Exit codes: `0` success, `1` the API returned an error (printed on stderr, with validation messages per parameter), `2` the API could not be reached or its response could not be read. Other non-zero codes are usage errors.

---

## Configuration

Standard .NET configuration: `appsettings.json`, then environment variables (`__` for `:`, e.g. `MarketData__Binance__RestBaseUrl`), then command-line arguments.

### Shared (API and worker)

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings:MarketData` | (required) | Npgsql connection string. In containers add `GSS Encryption Mode=Disable` (the slim images have no Kerberos library) |
| `MarketData:Binance:RestBaseUrl` | `https://api.binance.com` | Spot REST base URL (klines backfill in the worker, exchangeInfo in the API) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Enables OTLP export of metrics (and API traces). Meter name `MarketData` |

### API (`src/MarketData.Api`)

| Key | Default | Description |
|---|---|---|
| `Admin:ApiKey` | empty | Key for `X-Api-Key`. **Empty means every admin request is rejected**. Development settings use `dev-admin-key` |
| `MarketData:Binance:CacheDuration` | `01:00:00` | How long the list of tradable symbols from exchangeInfo is reused |
| `MarketData:Cache:RecentAbsoluteExpiry` | `00:01:00` | Expiry of cached results whose window can still change |
| `MarketData:Cache:SettledAfter` | `2.00:00:00` | A window ending this long before the newest candle is treated as settled (outside the gap-scan window) |
| `MarketData:Cache:SettledSlidingExpiry` | `01:00:00` | Sliding expiry of settled results |
| `MarketData:Cache:SizeLimit` | `200000` | Cache size in units (1 per result plus 1 per point or series item) |
| `ASPNETCORE_HTTP_PORTS` | `8080` in the image | Listening port |

### Ingestion worker (`src/MarketData.Ingestion`)

| Key | Default | Description |
|---|---|---|
| `MarketData:Binance:SocketBaseUrl` | `wss://stream.binance.com:9443` | Spot WebSocket base URL |
| `MarketData:HistoryDepth` | `35.00:00:00` | History every active symbol should have, counted back from now. At worker startup and when a symbol is added or reactivated, missing history before the oldest stored candle is fetched (a symbol without data starts at this depth). Replaces the former `BackfillOnAdd` (24h); an old `BackfillOnAdd` setting is ignored |
| `MarketData:GapScanInterval` / `GapScanWindow` | `00:05:00` / `2.00:00:00` | Gap-scan cadence and how far back it looks |
| `MarketData:ReconcileInterval` | `00:01:00` | Periodic reconcile of active symbols against live subscriptions |
| `MarketData:ChannelCapacity` | `10000` | In-memory buffer between stream and writer (oldest dropped when full; the gap scan repairs drops) |
| `MarketData:WriteBatchSize` / `WriteBatchWindow` / `WriteMaxRetryDelay` | `500` / `00:00:01` / `00:00:30` | Writer batching and retry cap |
| `MarketData:FreshnessThreshold` | `00:02:00` | Readiness fails when an active symbol's newest candle is older than this |
| `MarketData:Telemetry:ConsoleExporter` | `false` | Print metrics to the console |
| `Kestrel:Endpoints:Health:Url` | `http://*:8081` | Health endpoint (`/health/live`, `/health/ready`, JSON with each check's status) |

### Compose (`.env`)

See [.env.example](.env.example): `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_PORT`, `ADMIN_API_KEY`, `API_PORT`, `BINANCE_REST_URL`, `BINANCE_SOCKET_URL`, `OTEL_EXPORTER_OTLP_ENDPOINT`.

---

## Database initialization

The schema and the seed data (the three initial symbols) come only from **EF Core migrations, applied by a migration bundle** ([ADR-008](docs/adr/008-migration-bundle-init-container.md)). The API and the worker never migrate. At startup they only check that no migration is pending, and report **not ready** ("schema behind: run the migrator") until it has run.

- **Docker Compose:** the `migrator` service runs automatically before `api` and `ingestion`. To re-run it: `docker compose run --rm migrator`. It is idempotent and takes an EF migration lock, so concurrent runs are safe.
- **Locally without Docker:**
  ```bash
  dotnet tool restore
  export ConnectionStrings__MarketData="Host=localhost;Port=5432;Database=marketdata;Username=postgres;Password=postgres"
  dotnet ef database update --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure
  ```
- **Bundle by hand:**
  ```bash
  docker build --target migrator -t marketdata/migrator .
  docker run --rm marketdata/migrator --connection "Host=…;Database=marketdata;Username=…;Password=…"
  ```
- **Kubernetes:** run the same image as an `initContainer` on both Deployments, or as a pre-upgrade Job, with the migrator credentials:
  ```yaml
  initContainers:
    - name: migrator
      image: marketdata/migrator:<version>
      args: ["--connection", "$(MIGRATOR_CONNECTION)"]
      env:
        - name: MIGRATOR_CONNECTION
          valueFrom: { secretKeyRef: { name: marketdata-db, key: migrator } }
  ```
- **Separate roles (production).** Compose uses one superuser for simplicity. In production the migrator owns the schema, and the apps get data access only (`LISTEN`/`NOTIFY` needs no grants):
  ```sql
  CREATE ROLE marketdata_migrator LOGIN PASSWORD '…';
  CREATE ROLE marketdata_app      LOGIN PASSWORD '…';
  CREATE DATABASE marketdata OWNER marketdata_migrator;
  \c marketdata
  GRANT CONNECT ON DATABASE marketdata TO marketdata_app;
  GRANT USAGE ON SCHEMA public TO marketdata_app;
  ALTER DEFAULT PRIVILEGES FOR ROLE marketdata_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE ON TABLES TO marketdata_app;
  ALTER DEFAULT PRIVILEGES FOR ROLE marketdata_migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO marketdata_app;
  ```
  Run the migrator as `marketdata_migrator` and the apps as `marketdata_app`.
- **Rolling deploys:** the init container runs while the previous release still serves, so every migration must be backward compatible with it (expand/contract: add a nullable column → deploy → backfill → tighten later).
- **New migration:** `dotnet ef migrations add <Name> --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure`. CI fails if the model has changes without a migration.

---

## Running locally without Docker

Needs the .NET 10 SDK (`global.json` pins 10.0.402 with `latestFeature` roll-forward) and a PostgreSQL 17 instance.

```bash
dotnet tool restore
export ConnectionStrings__MarketData="Host=localhost;Port=5432;Database=marketdata;Username=postgres;Password=postgres"
dotnet ef database update --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure
dotnet run --project src/MarketData.Ingestion          # health on http://localhost:8081
dotnet run --project src/MarketData.Api                # http://localhost:5067 (Development: admin key dev-admin-key)
dotnet run --project src/MarketData.Client -- --url http://localhost:5067 avg BTCUSDT
```

## Development

```bash
dotnet build MarketData.slnx                                   # warnings are errors
dotnet test --solution MarketData.slnx                         # needs Docker (Testcontainers PostgreSQL)
dotnet test --project tests/MarketData.Core.Tests --filter-class "MarketData.Core.Tests.Domain.TimePeriodTests"
```

Tests run on Microsoft.Testing.Platform (xUnit v3), so use `--solution`/`--project` and xUnit's `--filter-class`/`--filter-method`. Package versions are central (`Directory.Packages.props`) with lock files; commit `packages.lock.json` after changing packages.

| Project | Contents |
|---|---|
| `src/MarketData.Contracts` | DTOs shared by the API and the client (DataContract names match the JSON) |
| `src/MarketData.Core` | Domain (`TimePeriod`, `SymbolName`), SMA math, analysis service, repository interfaces |
| `src/MarketData.Infrastructure` | EF Core context and migrations, SQL rollups, Binance adapters, `LISTEN/NOTIFY` |
| `src/MarketData.Api` / `src/MarketData.Ingestion` / `src/MarketData.Client` | The three executables |
| `tests/*` | Core unit tests; Infrastructure, API (incl. client) and Ingestion integration tests |

CI (`.github/workflows/ci.yml`) builds and tests in Release with locked restores, checks for un-migrated model changes, builds all four Docker targets, and smoke-tests the migration bundle against a fresh PostgreSQL.

## Operations

- **Health:**
  - API: `/health/live` and `/health/ready` (database + schema).
  - Worker (`:8081`, not published by compose): `/health/ready` also checks the WebSocket, the `symbol_changes` listener, and freshness (newest candle under 2 minutes old).
  - Both images have Docker `HEALTHCHECK`s on `/health/ready`.
- **Metrics** (OpenTelemetry meter `MarketData`):
  - `marketdata.candles.ingested`, `marketdata.ingest.lag`, `marketdata.ws.reconnects`
  - `marketdata.backfill.candles`, `marketdata.gap.minutes`
  - `marketdata.channel.depth` / `.dropped`, `marketdata.writer.failures`
  - `marketdata.pg_listener.reconnects`, `marketdata.notifications.received`, `marketdata.analysis_cache.lookups`
- **Scaling:** the API scales horizontally. Run **one** ingestion worker ([ADR-009](docs/adr/009-single-ingestion-replica-no-leader-election.md)); the orchestrator restarts it on failure and the startup backfill repairs the gap. Two workers overlapping (e.g. a rolling deploy) store each candle once, at the cost of a duplicate socket while it lasts.
- **Completeness:** restarts and outages are repaired automatically. On start and after reconnects the worker backfills from its newest candle, and every 5 minutes it scans the last 48h for gaps and refills them from Binance REST.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Worker not ready, logs show HTTP **451** or connection failures to Binance | Binance blocks some regions (including the US and many cloud/CI runners). Set `BINANCE_REST_URL=https://data-api.binance.vision` and `BINANCE_SOCKET_URL=wss://data-stream.binance.vision` (or the `MarketData:Binance:*` keys) |
| `/health/ready` returns 503 "Database schema is behind … Run the migrator" | Migrations haven't been applied: `docker compose run --rm migrator` or `dotnet ef database update` |
| Admin requests always return 401 | `Admin:ApiKey` / `ADMIN_API_KEY` is empty or different from the `X-Api-Key` header |
| `404 No data available` right after adding a symbol | The worker is still backfilling; wait a few seconds. `symbols list` shows `lastCandleAt` once data is in |
| Results lag the clock by up to a minute | By design: analysis is anchored at the newest stored candle, which closes about a second after each minute ends |
| Port 5432 or 8080 already in use | Set `POSTGRES_PORT` / `API_PORT` in `.env` |
| `libgssapi_krb5.so.2` message in container logs | Add `GSS Encryption Mode=Disable` to the connection string (compose already does) |
