
# F-037 — Durable Azure Git source acquisition

F-037 replaces the previous synchronous Azure Git fetch with an opt-in PostgreSQL worker queue. Completion means only that a SOURCE bundle has been acquired and SHA-256 verified. It does not authorize migration or GitHub writes.

## Enabling

The flags GitSourceBundles:Enabled, AzureGitFetch:Enabled, AzureGitQueue:Enabled, and Jobs:Enabled must all be true. The queue is off by default. Configure absolute non-symlinked SourceRoot and BundleRoot paths on encrypted, private storage volumes. When the queue is enabled, the old synchronous F-036 fetch endpoint returns HTTP 409 UseAzureGitAcquisitionQueue to avoid bypassing leases.

## Admin API

POST /api/incremental-preparations/{preparationId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/source-acquisitions

Request body: {"expectedReconciliationSha256":"64 lowercase hex"}

Returns 202 Accepted with a job ID and polling URI. GET endpoints under /operation-plans/{planId}/source-acquisitions, /source-acquisitions/{jobId} and /source-acquisitions/{jobId}/events give bounded job status and audit history. JWT admin/superadmin and password_changed are required. No request can supply credentials, a remote URL or filesystem path.

## Checkpointing and recovery

Queued/Pending -> Running/Pending -> Running/MirrorVerified -> Completed/BundleVerified.

A leased worker performs the read-only Azure Git fetch, checks every ref and Git object, and commits MirrorVerified with a compare-and-swap lease fence. It then calls the existing F-035 bundle export, checks the immutable PostgreSQL SHA-256, and commits BundleVerified. Crash after the first checkpoint resumes from MirrorVerified without another fetch. A crash after F-035 commits is recovered by its idempotent evidence read. A stale worker can never append a successful checkpoint or a failure event.

Cross-replica concurrency is at most TWO active leases, serialized through a global PostgreSQL advisory transaction lock and FOR UPDATE SKIP LOCKED. Leases run up to five minutes; work has a four-minute deadline. There are four total attempts, bounded retry backoff and expiry recovery. SQL CHECK forbids ready_for_execution=true and fabricated completion. Event records contain only safe codes.

The Git mirror and bundle remain capped at 512 MiB each, and subprocess timeouts and clean credentials handling from F-035/036 continue to apply. These are whole-mirror checkpoint boundaries; byte-range resumable Git pack downloads are NOT implemented. Disk quota reservations, orphan artifact garbage collection, independent runtime container and production Azure DevOps tenant smoke tests remain future hardening work. The feature does not support on-prem TFS and never writes to GitHub.

Tests cover unit processor phases, retries, lost leases, and PostgreSQL multi-replica idempotency, crash reclaim, event order, JWT and SQL safety. Existing real-Git F-035/036 tests stay part of the pipeline.
