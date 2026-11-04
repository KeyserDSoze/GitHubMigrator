# F-008 — Durable staging jobs, leases and audit (first vertical slice)

**Status:** A real PostgreSQL-backed asynchronous job engine now supports **two read-only-to-providers job types**: `StageSnapshot` (previously saved snapshot to F-013 staging) and `DiscoverSource` (Azure DevOps discovery → immutable snapshot checkpoint → F-013 staging). It is a foundation for later migration worker types, not a code/issue publisher and **not** plan approval.

## Operator sequence

1. Run a read-only source discovery to create the immutable snapshot. F-013 also retains the existing direct synchronous staging path for compatibility.
2. On **Discovery**, choose a snapshot and click **Queue background staging sync / Accoda sync staging in background**.
3. `POST /api/jobs/staging` returns **202 Accepted** and a durable job ID. Repeating the request for the same snapshot returns the **same** job, avoiding duplicate enqueue.
4. A hosted .NET worker polls PostgreSQL, atomically leases one job, reads the snapshot from the database and runs the existing F-013 normalizer. The UI polls its persisted status every two seconds while queued/running.
5. The job becomes `Succeeded` with the persisted staging result, or `Queued` with a bounded retry time on a transient error; permanent/max-attempt failures become `Failed`. The audit event ledger records transitions with attempt numbers and *controlled error codes only*.
6. Administrators can view the latest 25 jobs per source connection, one job's status and up to 100 most recent events. On page refresh the discovery workbench retrieves the saved status for the selected snapshot.

## PostgreSQL tables

- `migration_jobs`: fixed kind `StageSnapshot`, source connection, immutable source snapshot, unique idempotency key, requesting admin ID, state, max attempts, lease token/deadline, next available time, result JSON, timestamps and safe error code.
- `migration_job_events`: append-only `Queued`, `Claimed`, `RetryScheduled`, `Succeeded`, `Failed` entries. Event timestamps and attempt counters are persisted independently of frontend state.
- The existing `staging_assets` and `staging_snapshot_syncs` are the true normalization checkpoint; job success stores an immutable summary of the applied snapshot.

Indexes support unique idempotency, unique non-null lease ownership, ready-queue polling and source/job event history. DDL is additive to the development bootstrap; reviewed migrations and production schema upgrades are still required.

## Concurrency and recovery

- A single atomic PostgreSQL update selects the next `Queued` or expired `Running` job with `FOR UPDATE SKIP LOCKED`. Multiple API instances can safely share the same queue.
- Every attempt has a random lease token, an increasing attempt number and a 3-minute lease. Normal staging work is cancelled after 90 seconds, ensuring it finishes/fails before lease expiration. A process killed without finalizing its attempt leaves a lease which another worker can recover after expiry.
- Completion and failure are compare-and-swap fenced by the current lease token, attempt number, state and lease expiration. Late replies from dead/reclaimed workers **cannot overwrite** newer outcomes.
- Transient failures use exponential delays (10, 20, 40, 80 seconds) with a cap at 300 seconds; five attempts are allowed. Permanent validation failures fail closed immediately.
- A killed fifth attempt is marked failed upon the next queue poll, not left running forever.
- The F-013 sync is itself idempotent by snapshot and transactionally updates asset batches. If a process dies after staging commits but before the job completion event, the next attempt reads the same persisted sync and records the same result without copying/revising assets again.
- Job and audit records cannot contain raw provider exceptions, PATs or arbitrary error strings. They do not trigger provider writes.
- A built-in worker runs by default. Set `Jobs:Enabled=false` on API instances when deploying a dedicated host with `Jobs:Enabled=true`; only one process actually claims a given available job.

## REST endpoints

All endpoints require a completed first-login password change and `admin`/`superadmin` role.

| Method | Route | Result |
| --- | --- | --- |
| POST | `/api/jobs/staging` | Body `{ "snapshotId": "uuid" }`; 202 with job state/location. Repeated calls return same ID. |
| GET | `/api/jobs/{jobId}` | Durable status, attempts, next retry, final error code and staging result. |
| GET | `/api/jobs/{jobId}/events` | Up to 100 recent audit transitions, chronological display. |
| GET | `/api/jobs/connections/{sourceConnectionId}` | 25 latest jobs for a source connection. |

A new job cannot be used to bypass F-005 approval gates: the only supported kinds are `StageSnapshot` and the GET-only `DiscoverSource`. Actual GitHub writes and a general migration workflow remain unavailable.

## Tests and performance considerations

- Real PostgreSQL: eight concurrent enqueue requests; two workers claiming distinct jobs; lease loss and fenced stale completions; retry schedule and terminal failure; expired final attempt; immutable output; source normalization; reject secret-like error strings.
- Real ASP.NET + PostgreSQL: JWT admin RBAC, same job ID on repeat, worker execution and audit retrieval, no external provider traffic.
- Playwright: operator requests async staging and observes a queued-to-success state with staging counts.
- CI also runs .NET unit tests, Terraform validation and React compilation.

The poller is intentionally conservative: one in-flight job per worker host and a 2-second idle polling interval. Higher throughput, heartbeat for longer jobs, backpressure, workload partitioning, observability metrics, retention/compaction and worker-only deployment are needed before high-scale rollout. **No throughput/latency SLO is claimed without load tests.**

## Checkpointed source discovery (second slice)

`POST /api/jobs/discovery` accepts a verified Azure DevOps source `connectionId` and a UUID `requestId`. A partial unique index prevents concurrent queued/running scans for the same source, including separate browser tabs; an identical request ID is idempotent. The worker gives each scan a stable snapshot ID equal to its job ID, commits an immutable discovery inventory, persists a lease-fenced `SnapshotSaved` checkpoint, and then stages the result. On retry, it reads that same snapshot from PostgreSQL without repeating provider calls. Scan + stage has a four-minute deadline within a five-minute lease.

This is not yet *per Azure DevOps API page* checkpointing, true server-side delta collection, or scheduled auto-sync. For detailed API, safety boundaries and test coverage see [async-discovery.md](async-discovery.md).

## Follow-on slices

1. **Implemented initial slice:** execute read-only Azure DevOps discovery in durable jobs, checkpoint the complete source snapshot, preserve existing provider pagination. Next persist individual page cursors and source delta checkpoints for large deployments.
2. Stage pipeline definitions, Boards, dependency graph edges, normalized source/target mappings and immutable blob artifacts.
3. Add authorization-gated plan approval and durable write job definitions with per-resource idempotency and read-after-write reconciliation.
4. Build instrumented load tests, tenancy isolation and production EF migrations.
