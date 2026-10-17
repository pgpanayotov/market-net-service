# ADR-009: One ingestion replica, no leader election

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner
**Supersedes:** the leadership part of [ADR-007](007-separate-ingestion-worker-listen-notify.md) (advisory-lock leader with hot standby). The worker/API split and `LISTEN/NOTIFY` for admin changes stay as decided there.

## Context
ADR-007 made the ingestion worker elect a leader with `pg_try_advisory_lock`, so extra replicas could wait as hot standbys and take over within seconds. In practice that machinery buys little:
- **Two workers at once are harmless.** Every write is an idempotent upsert on `(symbol_id, open_time)`, and an update that would change nothing isn't written at all (P2). A duplicate costs one extra WebSocket and duplicate REST backfill calls, which is negligible at this scale. The overlap test added with this ADR confirms it.
- **Failover speed doesn't protect data.** When the only worker stops, the startup tail backfill refills exactly the missed minutes once it is back (P9: 5 minutes down, 0 gaps afterwards). A hot standby only shortens a delay that never shows in the stored data.
- **It added real complexity:**
  - leadership terms, plus a probe of the lock session;
  - services started and stopped per term;
  - a "standby" readiness state and an `is_leader` gauge;
  - a database outage cost a leadership term (P9 finding 3).

A single-instance guard (exit when the lock is held) was considered as a middle ground and dropped for the same reason. A rolling deploy briefly runs two workers anyway, and that is safe.

## Decision
- **Run one ingestion replica.** Its four services (`SymbolChangeListener`, `BinanceStreamCollector`, `CandleWriter`, `GapBackfiller`) are ordinary hosted services, started and stopped by the host.
- **No advisory lock and no leader/standby role.**
  - Readiness checks the database, the schema, the WebSocket, the listener and freshness. The freshness grace period runs from process start.
  - The `role` field and the `marketdata.worker.is_leader` gauge are removed.
  - So are the options `LeaderRetryInterval`, `LeaderCheckInterval` and `LeaderLockKey`.
- **Failures:** if the worker crashes, the orchestrator restarts it (`restart: unless-stopped` in compose, a Deployment in Kubernetes). An unhandled exception in any service stops the host, which is the .NET default, so a restart follows.
- **Rolling deploys may overlap the old and new worker for a few seconds.** Both may ingest during that window, which is harmless. Kubernetes doesn't need `Recreate`; it is optional if you want to avoid the duplicate socket.

## Options Considered

| Option | Complexity | Gap on worker failure | Duplicate work | Verdict |
|---|---|---|---|---|
| Leader election + hot standby (ADR-007) | High | ~seconds | None | Superseded: complexity without a data benefit |
| Single-instance guard (lock, exit if held) | Medium | Restart time; repaired by backfill | None | Rejected: protects against an overlap that is harmless |
| **One replica, no coordination** ✅ | Low | Restart time; repaired by backfill | Only during deploys | Chosen |
| Active-active (N replicas, all ingest everything) | Low | None | Constant ×N | Allowed but not needed at this scale |

## Consequences
- **Easier:** fewer moving parts. A database outage no longer causes a leadership change, readiness has one meaning, and there are fewer options and tests.
- **Harder:** nothing prevents a long-running second replica. If one is started by mistake it simply ingests in parallel, wasting one socket and duplicating REST calls. Metrics and Binance connection counts would show it.
- **Revisit when scaling past one worker** (more than about 1000 symbols, or isolation per symbol group): assign symbols to workers rather than electing a leader. Either use static sharding (`symbol_id % N`), or have each worker own a set of symbols through per-symbol advisory locks or leases, reclaiming them from dead workers in the 60s reconcile loop. Workers behind one NAT share Binance's per-IP limits, so true scale-out needs separate egress IPs.

## Action Items
1. [x] Remove `LeaderLock`, `LeadershipState`, `ILeaderService` and the standby role. Register the four services as hosted services.
2. [x] Readiness without a role; freshness grace from process start (`IngestionState.StartedAt`).
3. [x] Replace the leadership test with `OverlappingWorkersTests` (two workers, each candle stored once, both ready).
4. [x] Update ADR-007's status, the system design, the README, compose comments and CLAUDE.md.
