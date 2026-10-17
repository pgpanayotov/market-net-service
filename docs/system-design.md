# System Design: Market Data Collection & Analysis Service

Source: [requirements.md](requirements.md). Status: proposed, before implementation. Decisions are recorded in [docs/adr](adr/README.md); the design review findings R1–R8 are listed there and applied here. **Revision 2 (2026-10-18):** ingestion is split into its own worker and talks to the API through Postgres `LISTEN/NOTIFY` ([ADR-007](adr/007-separate-ingestion-worker-listen-notify.md)). Schema changes run only through an EF migration bundle in an init container ([ADR-008](adr/008-migration-bundle-init-container.md)).

---

## 1. Requirements

### 1.1 Functional
| # | Requirement | Notes |
|---|---|---|
| F1 | Collect live prices from the Binance Spot **multi-stream** WebSocket | Initial symbols: `BTCUSDT`, `ADAUSDT`, `ETHUSDT` |
| F2 | Persist to a relational DB through EF Core | PostgreSQL chosen (see §6) |
| F3 | Add, remove and list monitored symbols **at runtime** through admin endpoints | No restart; takes effect within seconds |
| F4 | `GET /api/{symbol}/24hAvgPrice` | Average over the last 24h, or over the available period if shorter |
| F5 | `GET /api/{symbol}/SimpleMovingAverage?n=&p=&s=` | `p` ∈ `1w, 1d, 30m, 5m, 1m`; `s` optional, defaults to now |
| F6 | JSON **and** XML on every endpoint, chosen by the `Accept` header | Errors too |
| F7 | Console client with the same analysis features, calling the HTTP API | |
| F8 | Dockerfile + docker-compose + README; `engineering-notes.md` | |

### 1.2 Non-functional (assumed; the spec doesn't give numbers)
- **Scale:** 3 symbols to start, designed to handle about 100 without changes.
- **Latency:** analysis endpoints p95 < 100 ms for a 24h window; < 500 ms for the largest SMA (`n=1000, p=1w`).
- **Freshness:** stored data at most about 1 minute behind the market.
- **Completeness:** no lasting gaps after WebSocket disconnects or DB outages (self-healing).
- **Availability:** the API scales horizontally. Ingestion has one active instance (an optional hot standby takes over through an advisory lock). API and ingestion fail independently. Restarts must not lose data.
- **Cost/ops:** runs locally with `docker compose up` and no external dependencies apart from Binance.

### 1.3 Constraints
- .NET 10, ASP.NET Core, EF Core. One developer, assisted by AI.
- Binance public market streams need no API key. Binance closes each connection after 24h and limits a connection to 1024 streams and 5 incoming messages/s.

---

## 2. Key decision: what "price data" we store

The analysis endpoints work on **time buckets** (1m to 1w). Raw trades are high-volume (BTCUSDT averages tens of trades per second), and every query would have to re-aggregate them.

**Decision: subscribe to `<symbol>@kline_1m` and persist each closed 1-minute candle** (OHLCV, where `k.x == true`).

| Option | Rows/symbol/day | 24h avg | SMA | Verdict |
|---|---|---|---|---|
| `@trade` / `@aggTrade` ticks | ~100k–2M | Trade-weighted, heavy | Must aggregate ticks | Too much data for the questions asked |
| `@miniTicker` (1s snapshots) | 86,400 | Easy | Must aggregate | Workable, but 60× more rows for no gain |
| **`@kline_1m` (closed)** | **1,440** | **Mean of 1m closes = time-weighted average** | **Roll up 1m → p** | ✅ Smallest data, exact bucket boundaries, can be backfilled through REST |

The deciding advantage is that the same 1m candles exist on the REST endpoint `GET /api/v3/klines`. Any gap (disconnect, outage, a newly added symbol) can be **backfilled with identical data**, so the store becomes self-healing (§7).

> **Assumption A1:** "average price" means the mean of 1-minute close prices, which is a time-weighted average. If a trade- or volume-weighted mean is wanted, VWAP can be computed from the stored `QuoteVolume / Volume` without changing the schema.

---

## 3. High-level architecture

```
 Binance Spot WS (combined streams)          Binance REST /api/v3/klines
 wss://stream.binance.com:9443/stream        (tail backfill + gap scan)
            │                                          ▲
            ▼                                          │
 ┌──────────────── MarketData.Ingestion (Worker, 1 active; pg_try_advisory_lock) ─────────────────┐
 │  BinanceStreamCollector ──Channel<Candle>──► CandleWriter ──┐        GapBackfiller ────────────┘ │
 │          ▲                                  (batched upsert │                                    │
 │          │ subscribe/unsubscribe             + pg_notify)   │                                    │
 │  SymbolChangeListener  (LISTEN symbol_changes; reconcile on connect + every 60s)                 │
 └──────────▲───────────────────────────────────────────────┼──────────────────────────────────────┘
            │ NOTIFY symbol_changes                          │ upsert candles_1m
            │ (same tx as symbols write)                     │ NOTIFY candles_written {symbol, latestOpenTime}
 ┌──────────┴────────────────────────────────────────────────▼──────────────┐
 │ PostgreSQL 17     symbols │ candles_1m │ __EFMigrationsHistory            │◄── migrator (efbundle)
 └──────────▲────────────────────────────────────────────────┬──────────────┘    init container: runs to
            │ symbol writes + pg_notify, SQL rollups          │ LISTEN candles_written     completion before
            │                                                ▼                            api + ingestion
 ┌─────────────────── MarketData.Api (stateless, N replicas) ───────────────────┐
 │  SymbolsController (admin) ───► Binance REST /api/v3/exchangeInfo (validate) │
 │  PricesController (JSON|XML) ─► PriceAnalysisService + IMemoryCache          │
 │  CandlesWrittenListener ─► SymbolDataState (latest per symbol, change tokens)│
 └──────────▲───────────────────────────────────────────────────────────────────┘
            │ HTTP
   Console client / curl / admin
```

**Why two processes ([ADR-007](adr/007-separate-ingestion-worker-listen-notify.md)):** the API becomes stateless and scales horizontally, API deploys and crashes no longer interrupt ingestion, and the singleton WebSocket collector has a home of its own. Cross-process signals use Postgres `LISTEN/NOTIFY`, which needs no new infrastructure and is transactional with the admin write. Notifications are treated as **hints**: both sides reconcile from the DB on every (re)connect, so a missed message only delays convergence.

**Two notification channels:**
| Channel | Direction | Sent | Payload | Receiver action |
|---|---|---|---|---|
| `symbol_changes` | API → worker | In the **same transaction** as the `symbols` update (delivered only on commit) | `{"op":"add"\|"remove","symbol":"SOLUSDT","version":…}` | Subscribe or unsubscribe that symbol; on add, start backfill |
| `candles_written` | worker → every API replica | After each committed batch, one per symbol | `{"symbol":"BTCUSDT","latestOpenTime":"…"}` | `latest = max(latest, payload)`; fire the symbol's cache change token |

### 3.1 Solution layout
```
src/
  MarketData.Contracts/       DTOs shared by API and client (AvgPriceResponse, SmaResponse, SymbolDto, TimePeriod)
  MarketData.Core/            Domain: TimePeriod parsing/alignment, IPriceAnalysisService, ISymbolRegistry, interfaces
  MarketData.Infrastructure/  EF Core DbContext + migrations + IDesignTimeDbContextFactory (used by the bundle),
                              Binance.Net adapters, repositories, raw SQL rollups, PgNotificationListener/Publisher
  MarketData.Api/             ASP.NET Core host: controllers, CandlesWrittenListener, SymbolDataState, health checks
  MarketData.Ingestion/       Worker Service: collector, writer, gap backfiller, SymbolChangeListener, leader lock,
                              minimal /health endpoints on internal port 8081
  MarketData.Client/          Console app (System.CommandLine + typed HttpClient)
tests/
  MarketData.Core.Tests/                 Unit: period parsing, bucket alignment, SMA math
  MarketData.Api.IntegrationTests/       WebApplicationFactory + Testcontainers Postgres
  MarketData.Ingestion.IntegrationTests/ Worker host + Testcontainers Postgres + fake stream / stubbed REST
Dockerfile                    Multi-stage; targets: api, ingestion, migrator, client
.config/dotnet-tools.json     Pins dotnet-ef (bundle build)
```

**Library:** use **Binance.Net** (JKorf) for both WebSocket and REST. It handles combined streams, automatic reconnects, the 24h forced reconnect, typed kline models and per-subscription unsubscribe. A hand-rolled `ClientWebSocket` stays possible behind `IMarketDataStream`, but it buys nothing here.

---

## 4. Data model

```
symbols                                         candles_1m
─────────────────────────────────────           ────────────────────────────────────────────────────
id            int  PK identity                  symbol_id     int          PK(1) FK → symbols.id
name          varchar(20) UNIQUE (upper-case)   open_time     timestamptz  PK(2)   -- UTC, minute-aligned
is_active     bool  NOT NULL                    open          numeric(28,10)
created_at    timestamptz                       high          numeric(28,10)
updated_at    timestamptz                       low           numeric(28,10)
                                                close         numeric(28,10)
                                                volume        numeric(38,10)       -- base asset
                                                quote_volume  numeric(38,10)       -- for VWAP (A1)
                                                trade_count   int
                                                received_at   timestamptz          -- ingestion lag metric
```

- **PK `(symbol_id, open_time)`** is also the only index needed: every query is "one symbol, one time range". It also makes writes **idempotent**, because the WS feed, backfill and replays all upsert with `INSERT … ON CONFLICT (symbol_id, open_time) DO UPDATE`.
- **`decimal` / `numeric`, never `double`**, because crypto prices such as ADA at 8+ decimal places need exact arithmetic.
- **Removing a symbol is a soft delete (`is_active = false`).** History stays queryable, and adding the symbol back reactivates the row and backfills the gap.
- **Seeding:** the three initial symbols are **reference data in the initial migration** (`HasData`), so the migration bundle seeds them. No app writes schema or seed data at startup ([ADR-008](adr/008-migration-bundle-init-container.md)). After that, symbols are managed only through the admin API.
- **Roles:** `marketdata_migrator` owns the schema (DDL). `marketdata_app` (API + worker) has DML on both tables plus `LISTEN`/`NOTIFY`.

**Volume:** 1,440 rows/symbol/day × ~120 B ≈ 170 KB/symbol/day. For 3 symbols that's about 190 MB over 1 year; for 100 symbols, about 52 M rows and about 6 GB per year. Postgres handles this easily with the PK index.

---

## 5. API design

All controllers use `[ApiController]`. Content negotiation:

```csharp
builder.Services.AddControllers(o =>
{
    o.RespectBrowserAcceptHeader = true;
    o.ReturnHttpNotAcceptable   = true;          // 406 for unsupported Accept
})
.AddXmlDataContractSerializerFormatters();      // application/xml + text/xml
builder.Services.AddProblemDetails();           // errors also negotiated (JSON/XML)
```

> **Why controllers, not Minimal APIs:** Minimal APIs write JSON only. XML negotiation needs MVC output formatters, and F6 applies to *all* endpoints. DTOs are plain classes with public setters and `[DataContract]`/`[DataMember]` so XML element names are stable.

### 5.1 Analysis endpoints

`GET /api/{symbol}/24hAvgPrice`
```json
{ "symbol": "BTCUSDT", "averagePrice": 67412.53, "from": "2026-10-17T17:20:00Z",
  "to": "2026-10-18T17:20:00Z", "dataPoints": 1440, "isFullPeriod": true }
```
- Window `[latest − 24h, latest)` over closed 1m candles, where `latest = max(open_time) + 1m` for the symbol. It is anchored at the newest stored data, not the wall clock ([ADR-006](adr/006-analysis-semantics.md)), so removed symbols stay queryable and a candle that hasn't landed yet is never half-counted. If less than 24h of data exists, `from` is the first available candle and `isFullPeriod = false` (meets "or over the available period").

`GET /api/{symbol}/SimpleMovingAverage?n=20&p=5m&s=2026-10-18T12:07:00Z`
```json
{ "symbol": "BTCUSDT", "n": 20, "period": "5m", "referenceTime": "2026-10-18T12:05:00Z",
  "simpleMovingAverage": 67390.11, "dataPointsUsed": 20,
  "points": [ { "time": "2026-10-18T10:25:00Z", "close": 67301.0 }, "…" ] }
```

**SMA semantics (assumption A2, to be documented in README):**
1. Parse `p` into a `TimePeriod` (`1m, 5m, 30m, 1d, 1w`). Anything else returns 400.
2. Align `min(s ?? UtcNow, latest)` **down** to a bucket boundary: `referenceTime = floor(min(s, latest), p)`. Only **complete** buckets ending at or before `referenceTime` count, so a half-formed bucket never skews the result. Day buckets start at 00:00 UTC; week buckets start Monday 00:00 UTC (same as Binance `1w`).
3. Take the **last `n` buckets** in `[referenceTime − n·p, referenceTime)`. Each bucket's value is the **close of its last 1m candle**.
4. SMA = arithmetic mean of those bucket closes.
5. Missing buckets (pre-history or gaps) are skipped. The response states `dataPointsUsed < n` instead of failing. `points` lets the client show or plot the inputs (`?includePoints=false` can omit it).

**Rolling series (`?series=true`, optional `&count=m`):**

`GET /api/BTCUSDT/SimpleMovingAverage?n=20&p=5m&series=true&count=3`
```json
{ "symbol": "BTCUSDT", "n": 20, "period": "5m", "referenceTime": "2026-10-18T12:05:00Z",
  "series": [
    { "time": "2026-10-18T11:55:00Z", "simpleMovingAverage": 67371.40, "dataPointsUsed": 20 },
    { "time": "2026-10-18T12:00:00Z", "simpleMovingAverage": 67380.92, "dataPointsUsed": 20 },
    { "time": "2026-10-18T12:05:00Z", "simpleMovingAverage": 67390.11, "dataPointsUsed": 20 } ] }
```
- Returns `m` SMA values (default `m = min(n, 100)`), one per bucket, ending at `referenceTime` and in ascending time order. Each value is the SMA(n) of the `n` buckets ending at its `time`. The last element equals the single-value response.
- One query fetches the last `n + m − 1` buckets. C# computes the series in O(n + m) with a sliding window sum (exact with `decimal`).
- When fewer than `n` buckets exist for a series element, that element is still emitted with its actual `dataPointsUsed`. Elements with 0 points are omitted.
- `series=false` (default) keeps the single-value shape above, so existing callers don't change. In series mode `points` is not returned (the series is the useful output). `simpleMovingAverage`/`series` are mutually exclusive in the DTO (`SmaResponse` has both, nullable; XML omits null elements).

Validation: `p` codes are case-sensitive (`1M` means a month by convention, so it is rejected). `1 ≤ n ≤ 1000`, `1 ≤ count ≤ 1000` (only valid with `series=true`, otherwise 400), so a query reads at most 1,999 buckets. `s` is ISO-8601 (treated as UTC when there's no offset) and must not be in the future.

### 5.2 Admin endpoints
| Method | Route | Body | Result |
|---|---|---|---|
| GET | `/api/admin/symbols` | — | 200 `[ { name, isActive, createdAt, lastCandleAt } ]` |
| POST | `/api/admin/symbols` | `{ "symbol": "SOLUSDT" }` | 201, or 200 if reactivated; 400 if not a TRADING spot symbol per `exchangeInfo`; 409 if already active |
| DELETE | `/api/admin/symbols/{symbol}` | — | 204; 404 if unknown |

- Protected by an **`X-Api-Key`** header (key from config/env) through an API-key `AuthenticationHandler` + `[Authorize(Policy = "Admin")]` ([ADR-004](adr/004-mvc-controllers-for-content-negotiation.md)). Read endpoints are public.
- Route note: `/api/admin/symbols` has a literal 2nd segment and never matches `/api/{symbol}/24hAvgPrice`.
- **Writes are asynchronous with respect to ingestion.** POST and DELETE update `symbols` and `pg_notify('symbol_changes', …)` in one transaction. 201/204 means "persisted"; the worker subscribes or unsubscribes within about 1s, or at its next reconcile if it was down. `lastCandleAt` in `GET` shows when ingestion has actually started.

### 5.3 Errors (RFC 7807 ProblemDetails, JSON or XML)
Errors from MVC are negotiated by the XML formatters. Errors raised outside MVC (auth challenges, unmatched routes, the exception handler) go through a custom `XmlProblemDetailsWriter`, because the default `IProblemDetailsService` writes JSON only.

| Case | Status |
|---|---|
| Invalid `n`, `p` or `s` | 400 |
| Symbol never configured | 404 |
| Symbol configured but no data yet in window | 404 with `detail: "No data available"` (a 200 with nulls is easier to misread) |
| `Accept` not JSON/XML | 406 |
| Missing/invalid admin key | 401 |

---

## 6. Deep dive

### 6.1 Storage: PostgreSQL over SQLite
| | PostgreSQL | SQLite |
|---|---|---|
| Concurrent writer (collector) + readers (API) | ✅ MVCC | ⚠️ single writer, WAL needed |
| Time bucketing in SQL (`date_bin`, `DISTINCT ON`) | ✅ | ❌ manual, slower |
| docker-compose story | ✅ official image | n/a (file) |
| Growth path (partitioning, TimescaleDB) | ✅ | ❌ |

Npgsql EF Core provider; schema through **EF Core migrations**.

### 6.2 SMA rollup query (one round-trip, done in the DB)
```sql
SELECT bucket, close FROM (
  SELECT DISTINCT ON (bucket)
         date_bin(@step, open_time, TIMESTAMPTZ '2000-01-03 00:00Z') AS bucket,  -- a Monday → week alignment
         close
  FROM candles_1m
  WHERE symbol_id = @symbolId AND open_time >= @from AND open_time < @to
  ORDER BY bucket DESC, open_time DESC
) b
ORDER BY bucket DESC
LIMIT @limit;   -- n, or n + count − 1 for series=true
```
`@from = referenceTime − limit·p`. The average (or the sliding-window series) is computed in C# from the returned rows (also yields `points`). Worst case `n=1000, count=1000, p=1w` scans about 20 M index rows; the `n`/`count` caps and the caching in §6.4 keep this within bounds. If it becomes hot, add rollup tables (§9).

This query sits in `ICandleQueryRepository` in Infrastructure (raw SQL via `FromSql`/Dapper-style projection). Core only sees `IReadOnlyList<BucketClose>`, which keeps the bucket math unit-testable.

### 6.3 Ingestion pipeline (`MarketData.Ingestion`)
0. **Leadership:** at start the worker takes `pg_try_advisory_lock(<const>)` on a dedicated connection. If it doesn't get the lock, it stays in standby (readiness reports `standby`) and retries every 10s. Only the leader runs steps 1–4.
1. **`BinanceStreamCollector`** (BackgroundService) loads active symbols, then calls `SubscribeToKlineUpdatesAsync(symbols, KlineInterval.OneMinute, …)`. Binance.Net multiplexes these over the combined-stream connection.
2. The callback filters `Data.Final == true` and maps to `Candle`, then `channel.Writer.TryWrite(candle)`. The callback does **no I/O**.
3. **`CandleWriter`** reads the channel in batches (up to 500 rows or 1s), upserts in a single statement, then sends `pg_notify('candles_written', …)` once per symbol in the batch. It also updates `lastCandleAt` in memory for health and metrics. Backfilled candles notify the same way.
4. **Runtime symbol changes:** `SymbolChangeListener` holds a dedicated, non-pooled `NpgsqlConnection` (`KeepAlive=30`). On every (re)connect it runs **`LISTEN symbol_changes` first, then a full reconcile** (DB active symbols vs. live subscriptions), so nothing falls between the two. It then loops on `WaitAsync(ct)`. Each notification subscribes or unsubscribes that symbol only (`UpdateSubscription.CloseAsync()`), and other streams keep running. Handlers are idempotent. A **60s reconcile** also runs, so a missed notification heals itself.

**API side:** `CandlesWrittenListener` in each API replica follows the same connect sequence (`LISTEN candles_written`, then reload `max(open_time)` per symbol and clear the cache) and updates `SymbolDataState`.

> **Constraint:** `LISTEN` needs a session-level connection. Any pooler in front of Postgres (PgBouncer) must run in session mode for these connections, or they must bypass it.

### 6.4 Caching
- Data changes once per minute per symbol, so `IMemoryCache` with key `(endpoint, symbol, params)`. Entries are **invalidated by a per-symbol change token that fires when the replica receives `candles_written` for that symbol**. All tokens fire on listener reconnect, and each entry has a 10-min absolute cap as a safety net against a missed notification. A timer-based expiry would cache stale data, because the final kline lands a few seconds after the minute boundary ([ADR-006](adr/006-analysis-semantics.md)).
- SMA requests whose `referenceTime < latest − 2 min` are **immutable**: cache for 1h (sliding).
- Cache only after DB success, never cache errors. Each API replica keeps its own cache. `NOTIFY` broadcasts to every listener, so no distributed cache is needed.

### 6.5 Error handling & retries
| Failure | Handling |
|---|---|
| WS disconnect / 24h forced close | Binance.Net auto-reconnects. On `ConnectionRestored`, trigger **gap backfill** |
| Gap in data (restart, outage, new symbol) | `GapBackfiller`: for each active symbol, `last = max(open_time)`; fetch `GET /api/v3/klines?interval=1m&startTime=last+1m` (1000/request) up to the last closed minute; upsert. Runs at startup, on reconnect, after DB recovery, and on symbol add (initial backfill capped at `MarketData:BackfillOnAdd`, default 24h) |
| Holes behind the newest candle (drops, partial batch failures) | **Gap scan** every 5 min, and immediately after drops or DB recovery: `generate_series` over the last 48h finds missing minutes, which are merged into ranges and fetched through REST ([ADR-005](adr/005-self-healing-ingestion.md)). Tail backfill alone can't see these |
| DB unavailable | Writer retries with exponential backoff (Polly, max 30s). Bounded channel (10k) with `DropOldest`; dropped candles are recovered by the gap scan once the DB is back |
| Binance REST 429/418 | Respect `Retry-After`; backfill is low-priority and resumable |
| Invalid symbol on add | API validates against `exchangeInfo` (cached 1h) before writing |
| Listener connection lost (either side) | Reconnect with exponential backoff, then `LISTEN` → full reconcile. Missed notifications only delay convergence |
| Worker down | Reads keep working on existing data; admin changes are persisted and applied on worker start (reconcile); gaps are backfilled. Data staleness alert fires |
| Two worker replicas | Only the advisory-lock holder ingests; the other is a hot standby |
| Schema behind the code | Startup guard (`GetPendingMigrationsAsync`) fails readiness with "schema behind: run migrator" |

Because backfill restores anything lost, the system tolerates restarts and transient failures without exactly-once delivery.

### 6.6 Console client
`MarketData.Client` uses **System.CommandLine**, a typed `HttpClient`, and DTOs from `MarketData.Contracts`.
```
marketdata avg BTCUSDT
marketdata sma BTCUSDT -n 20 -p 5m [-s 2026-10-18T12:00:00Z] [--points] [--series [--count 50]]
marketdata symbols list | add SOLUSDT | remove ADAUSDT      (uses MARKETDATA_ADMIN_KEY)
global: --url http://localhost:8080  --format json|xml  (sets Accept; shows negotiation works end to end)
```
It parses JSON or XML into the shared DTOs and prints a table. ProblemDetails map to readable errors with exit code ≠ 0.

---

## 7. Scale & reliability

**Load estimate.** Ingest is 3 symbols ≈ 3 msgs/s on the WS (kline updates about every 2s while a candle is open) and 3 DB rows/min. At 100 symbols: about 50 msgs/s and 100 rows/min, still trivial. Reads depend on users and are cheap thanks to the PK index and cache.

**Scaling path.**
- *API reads:* stateless and horizontally scalable. Each replica costs one extra DB connection (its listener).
- *Ingestion:* one active worker is enough well past 100 symbols. A standby replica gives fast failover.
- *Symbols > 1024:* shard subscriptions across several WS connections (Binance limit per connection). Replace the single advisory lock with per-shard locks and assign symbols by hash (ADR-007).

**Health & monitoring.**
- **API** `/health/live`, `/health/ready`: DB reachable, no pending migrations, `candles_written` listener connected.
- **Ingestion** (internal port 8081) `/health/live`, `/health/ready`: DB reachable, no pending migrations, role `leader|standby`. For the leader also: WS connected, `symbol_changes` listener connected, `max(lastCandleAt)` < 2 min for every active symbol.
- OpenTelemetry metrics: `candles_ingested_total{symbol}`, `ingest_lag_seconds` (`received_at − close_time`), `ws_reconnects_total`, `backfill_candles_total`, `channel_depth`, `pg_listener_reconnects_total{channel}`, `notifications_received_total{channel}`, `worker_is_leader`, HTTP RED metrics.
- Alert ideas: data staleness > 3 min, reconnects > 5/h, backfill failing, no leader for > 1 min, migrator job failed.
- Structured logging (Serilog/console JSON), with symbol in scope.

---

## 8. Deployment

One multi-stage `Dockerfile` (`sdk:10.0` build stage) with three service targets, plus a `client` target (`runtime:10.0`) for the console client:
| Target | Base image | Contents |
|---|---|---|
| `api` | `aspnet:10.0` | `MarketData.Api` |
| `ingestion` | `aspnet:10.0` (needed for its health endpoints) | `MarketData.Ingestion` |
| `migrator` | `runtime-deps:10.0` | `efbundle`, built with `dotnet ef migrations bundle --project src/MarketData.Infrastructure --startup-project src/MarketData.Infrastructure --self-contained -r linux-x64` |

`docker-compose.yml`:
```yaml
services:
  postgres:   { image: postgres:17, volumes: [pgdata:/var/lib/postgresql/data], healthcheck: pg_isready }
  migrator:   { build: { target: migrator }, restart: "no",
                command: ["./efbundle", "--connection", "${MARKETDATA_DB}"],
                depends_on: { postgres: { condition: service_healthy } } }
  ingestion:  { build: { target: ingestion },
                depends_on: { migrator: { condition: service_completed_successfully } } }
  api:        { build: { target: api }, ports: ["8080:8080"],
                depends_on: { migrator: { condition: service_completed_successfully } } }
  client:     { build: { target: client }, profiles: [tools] }   # docker compose run --rm client avg BTCUSDT
```
Env: `ConnectionStrings__MarketData` (all three), `Admin__ApiKey` (api).

**DB initialization ([ADR-008](adr/008-migration-bundle-init-container.md)):** there is **no `MigrateOnStartup`**. Schema and seed data (the three initial symbols, through `HasData`) are applied only by the `migrator` init container. It's idempotent (exits 0 when current) and lock-protected by EF Core, and it must complete before `api` and `ingestion` start.
- **Kubernetes:** `initContainers: [migrator]` on both Deployments (or a pre-upgrade Job), using the `marketdata_migrator` credentials.
- **Local without Docker:** `dotnet ef database update --project src/MarketData.Infrastructure`, or `docker compose run --rm migrator`.
- **Rolling deploys:** migrations must be backward compatible with the running release (expand/contract), because the init container runs while old pods still serve.
- **CI:** build the bundle, run it against an ephemeral Postgres, and fail on `dotnet ef migrations has-pending-model-changes`.

The README covers compose, local, k8s, roles and backfill behaviour.

---

## 9. Trade-offs & what to revisit as it grows

| Decision | Gain | Cost | Revisit when |
|---|---|---|---|
| Store 1m klines, not ticks | Small data, exact buckets, REST backfill | No sub-minute resolution; "average" = time-weighted | Sub-minute analytics needed → add `@aggTrade` table |
| Separate ingestion worker (ADR-007) | API scales out; isolated failures and deploys | Two deployables; admin changes apply asynchronously | Sharding needed past 1024 streams → per-shard advisory locks |
| Postgres `LISTEN/NOTIFY` for signals | No new infrastructure; transactional with the admin write; broadcast to all API replicas | At-most-once (covered by reconcile); needs session-mode connections | Durable or high-volume events needed → outbox + broker |
| Migration bundle as init container (ADR-008) | One explicit, lock-protected schema step; apps run with DML-only rights | Extra Docker stage and ordering config; expand/contract discipline | Long-running migrations → separate approved Job |
| Seed symbols through `HasData` | Deterministic; no startup writes | Changing defaults needs a migration (or just the admin API) | — |
| Postgres-specific SQL rollup | Fast, one round-trip | Not portable to SQLite; integration tests need Testcontainers | — (accepted) |
| On-the-fly rollups from 1m | No extra tables or jobs | `p=1w, n+count=2000` scans ~20M rows | p95 regresses → materialized 1h/1d rollup tables or TimescaleDB continuous aggregates |
| Backfill instead of durable queue | Simple, self-healing | Depends on Binance REST retention/rate limits | Data from non-backfillable sources |
| Controllers + XML formatters | F6 satisfied everywhere | More ceremony than Minimal APIs | — |
| API-key admin auth | Simple | No per-user audit | Multiple operators → JWT/OIDC + audit log |
| Soft-delete symbols (decided: history never purged) | History kept, cheap re-add | Candle history grows without bound | Retention policy → partition `candles_1m` by month and drop old partitions |

---

## 10. Assumptions & open questions
- **A1** "Average price" = mean of 1m closes (time-weighted). VWAP available if preferred.
- **A2** SMA = mean of the closes of the last `n` complete `p`-buckets ending at or before `floor(s, p)`. UTC alignment, weeks start Monday.
- **A3** Read endpoints are unauthenticated; admin endpoints use an API key.
- **A4** Symbol matching is case-insensitive (`btcusdt` = `BTCUSDT`). Responses always use upper-case.
- **Q1 (decided)** SMA supports both shapes: a single value by default, and a rolling series with `?series=true&count=m` (§5.1).
- **Q2 (decided)** Removing a symbol does **not** purge its history. It's a soft delete only, and the history stays queryable through the analysis endpoints.

## 11. Testing strategy (summary)
- **Unit:** `TimePeriod` parsing/alignment (edge cases: week boundaries, `s` exactly on a boundary, DST-irrelevant UTC), SMA with gaps, rolling-series sliding window (last element equals the single SMA; partial windows at the start of history), request validation (`count` without `series=true` gives 400).
- **Integration:** Testcontainers Postgres; the fixture applies migrations with `MigrateAsync()` (**test code only**) or runs the built bundle.
  - **API** (`WebApplicationFactory`): JSON vs XML vs 406, ProblemDetails in XML, admin writes emit `symbol_changes` (and none on rollback), `candles_written` updates `latest` and invalidates the cache, readiness fails on pending migrations.
  - **Ingestion** (worker host + `FakeMarketDataStream` + stubbed REST): a `symbol_changes` notification subscribes or unsubscribes, reconcile after a listener drop, idempotent upsert, gap backfill, the advisory lock keeps a second worker in standby.
  - **End to end:** both hosts on one container: admin add through the API leads to ingestion and then to analysis results.
- **Client:** run against the test server and check output for both formats.
- **Manual:** `docker compose up`, wait 5 min, call both endpoints with `curl -H "Accept: application/xml"`.
