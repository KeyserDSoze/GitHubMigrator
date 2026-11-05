# F-008 — Asynchronous Azure DevOps discovery and checkpointed staging

**Status:** source discovery runs as a durable F-008 job and now checkpoints project, Git ref and Graph user pages in PostgreSQL as a first granular recovery slice. This is the next slice of the database-centered engine in the AGIC design.

## Operator workflow

1. Connect and validate an `azure-devops-services` source. Only platform administrators with completed first-login password change can enqueue work.
2. In **Discovery**, choose a source and click **Queue discovery (background) / Accoda discovery (background)**. The UI sends `POST /api/jobs/discovery` with a saved source `connectionId` and client-generated `requestId` GUID.
3. The endpoint responds immediately with HTTP **202 Accepted** and `/api/jobs/{jobId}` as the status location. The request ID is idempotent, and the database permits only one queued/running source discovery per connection; a second request while one is active returns the existing job.
4. A worker acquires a PostgreSQL lease with an exclusive fencing token, reads Azure DevOps using the existing fixed-host GET-only discovery reader, and persists an immutable snapshot.
5. Each job owns a stable snapshot ID (**the job ID**). If its process exits after the snapshot is committed but before a checkpoint event, retry checks `migration_discoveries` for that ID and skips the Azure DevOps reads. The original snapshot cannot be overwritten.
6. The job records the durable `SnapshotSaved` checkpoint and normalizes the snapshot into `staging_assets` using F-013. Staging is itself idempotent.
7. The job reports `Succeeded` only after both immutable snapshot and staging commit, with observed/added/changed/missing counts. The UI automatically loads that snapshot and its staging result.
8. After reloading the browser, the UI can recover the queued/running job from the source's persisted job history and continue polling.

## REST/API

- `POST /api/jobs/discovery` body: `{ "connectionId": "<UUID>", "requestId": "<UUID>" }`. Requires saved, **Verified** Azure DevOps Services source. Rejects GitHub destinations, missing sources and zero IDs. Rate-limited using the external provider budget.
- `GET /api/jobs/{id}`: state, attempt count, stable `snapshotId` once persisted, retry time and safe error code.
- `GET /api/jobs/{id}/events`: `Queued`, `Claimed`, `SnapshotSaved`, `RetryScheduled`, `Succeeded` or `Failed`.
- `GET /api/jobs/connections/{sourceId}`: 25 most recent source jobs, used to recover the UI after refresh.
- Existing synchronous `POST /api/discoveries` remains for API compatibility in this increment, but the React Discovery button now uses the asynchronous job endpoint.

## Safety and concurrency

- The `ux_migration_jobs_active_discovery` partial unique index enforces **one active discovery per source connection**, including retries; separate connections can scan independently. Existing per-job request IDs retain idempotency even after completion.
- The worker's **whole scan + staging** deadline is 4 minutes and lease is 5 minutes. Jobs can survive browser disconnection but source calls are still bounded. On failure, provider timeout/unavailability can be retried with exponential backoff; invalid credentials, inadequate scope and invalid source inputs fail closed.
- A fenced `SnapshotSaved` transition only succeeds while the worker owns the current attempt. A stale worker cannot overwrite the snapshot checkpoint or finalize a newer attempt.
- No PAT, token, raw provider exception or secret reference is stored in the job record, audit events or API status. The saved connection secret is read transiently by the source reader.
- The discovery provider is **read-only**, and resulting source ACLs remain raw evidence, never effective GitHub grants. No migration plan is approved, no GitHub writes are performed.
- An interrupted scan **before** its immutable snapshot commit may still restart provider reads from its beginning. The deterministic snapshot ID protects the persisted boundary, not uncommitted provider pages.

## Paging and honest limitations

The Azure DevOps reader already uses bounded API pagination/continuation handling for projects, Git refs, Graph users and groups; the worker delegates to that implementation. **Project lists, Git refs, Graph users/groups, and direct group-membership pages now have durable checkpoints; project capabilities, repository enumeration and project/repository ACL sections are also checkpointed** (see [durable discovery pages](durable-discovery-pages.md)). TFVC, Boards, pipeline and delta collection are still outside checkpoint coverage. Project, Graph and security metadata still accumulate in memory during one provider scan; new leased Git ref reads instead persist source pages and retain only an externalized count. A 4-minute deadline, hard page/item caps and partial-inventory flags mean this is an operationally useful vertical slice, **not yet a large-enterprise streaming discovery implementation**.

Before claiming continuous, high-volume synchronization we must expand checkpoints to remaining provider endpoints, add streaming staging writes, source API rate-limit telemetry, partial-scan restart tests, scheduled periodic discovery or webhook triggers, true provider delta APIs where supported, stress tests and a reviewed schema migration. The previously implemented F-013 incremental hash sync currently compares *successive full source snapshots*. No live automatic scheduling has been enabled.

## Verification

- Unit: same job-owned snapshot ID resumes without provider credentials/HTTP reads; wrong source cannot reuse another connection's snapshot.
- PostgreSQL: concurrent idempotent requests; one active scan per source; stable read-after-crash; fenced checkpoint; separate later scans once the previous run is terminal.
- Real ASP.NET + PostgreSQL + stubbed Azure DevOps transport: JWT RBAC, verified source validation, read-only provider requests, job status, immutable saved snapshot, staging metrics and audit event order.
- Playwright: queue-to-snapshot UX and recovery of in-progress job after page reload.
