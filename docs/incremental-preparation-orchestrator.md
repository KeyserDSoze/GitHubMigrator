# F-026 — Durable incremental preparation, **review only**

F-026 turns the immutable F-025 per-snapshot journal into destination-bound,
durable **review-only preparation items**. It does not migrate Git objects,
delete GitHub repositories/refs, invite users, assign teams, compute effective
permissions or approve any operation. No GitHub HTTP client or provider secret
is injected into the preparation worker.

## Safety and identity model

A platform admin explicitly requests preparation with a committed
`snapshotId` and an independently **verified** GitHub Enterprise Cloud
`destinationConnectionId`. The service requires a verified Azure DevOps
source connection, existing version-1 staging journal, matching source/snapshot
and a non-superseded, most-recent snapshot. Each pair is unique:

- `incremental_preparations`: `(snapshot_id,destination_connection_id)`
  uniqueness, source ID, actor ID, state, available date, lease token and
  expiry, attempts/max 5, safe error code, review-item counts, and SHA-256
  evidence digest. The snapshot FK points to the committed staging sync.
- `incremental_preparation_items`: PK
  `(preparation_id,asset_type,source_id)`. Copies only the F-025 change kind,
  old/new SHA-256 fingerprint, revision and a review category. **Every item**
  has immutable initial state `NeedsReview`; `Security` flags ACLs,
  membership, groups and identities; `Removal` flags missing assets.
  The table carries **no target action, credential, Git pack bytes or ACL
  response JSON**.
- `incremental_preparation_events`: bounded auditable Queued/Claimed/
  RetryScheduled/Failed/Prepared events with attempt number, timestamp and
  allowlisted error code. Provider exception bodies are never persisted.

The phase-0 additive PostgreSQL bootstrap creates the tables and indexes on
pre-existing installations. Like earlier steps, this is **not a reviewed EF
production migration**.

## Worker and concurrent retries

`IncrementalPreparationBackgroundWorker` polls only if `Jobs:Enabled=true`
(the test fixture explicitly disables background work); it uses its own
scoped `IncrementalPreparationProcessor`, independent of discovery jobs.

- PostgreSQL `FOR UPDATE SKIP LOCKED` claims one eligible run per replica.
  A fresh UUID lease token, 5-minute bound and incrementing attempt number
  fence old workers. Expired work is reclaimable. An expired fifth attempt
  becomes a terminal failure.
- A preparation acquires the **same per-source advisory transaction lock**
  as F-013 staging, uses `READ COMMITTED` so it checks the latest committed
  source snapshot **after** acquiring the lock, and validates its live token
  before any output. A superseding source snapshot fails the old preparation.
- The F-025 journal aggregate must reconcile against its immutable sync
  summary. PostgreSQL performs one set-based
  `INSERT ... SELECT FROM staging_asset_changes` into review-only items
  under the same transaction. Count parity is checked before commit.
- The SHA-256 evidence root is calculated by streaming rows in ordered
  `(asset_type,source_id)` sequence, without allocating all deltas in
  application memory. The digest covers all evidence types, fingerprints,
  revisions, categories and the `NeedsReview` state.
- The final `Prepared` transition and audit event require a still-live,
  attempt-matched lease, **in that same transaction**. Stale attempts,
  newer snapshots, malformed or incomplete journals, crashes or deadlines
  cannot commit partially prepared items.
- Transient errors schedule exponential retry/backoff (up to five attempts),
  while stale snapshots, missing journals, wrong source IDs and integrity
  failures are permanent. Neither error paths nor audit events store
  exception messages.

No job is automatically approved, queued for GitHub execution or used to
delete or grant anything. A `Prepared` run is a _review workload_ only.

## Protected APIs

All endpoints require an authenticated admin/superadmin whose password has
been rotated; POST is rate-limited:

| Endpoint | Function |
| --- | --- |
| `POST /api/incremental-preparations` | Body `{snapshotId,destinationConnectionId}`. Idempotent `202 Accepted` + preparation state. |
| `GET /api/incremental-preparations/{id}` | Lease/retry state, counts, evidence digest. |
| `GET /api/incremental-preparations/{id}/items?take=50&afterType=ref&afterId=...` | Maximum 100 review items per page; keyset over composite PK; 409 until Prepared. |
| `GET /api/incremental-preparations/{id}/events` | Last 100 retry and completion events. |

Unknown preparations are 404; invalid pages/context 400, non-ready
items and superseded/unsupported journal 409. A pre-F-025 staging sync
cannot masquerade as an empty change set.

The IT/EN Discovery workbench lets an admin choose a verified GitHub
destination, enqueue the preparation and see attempts, terminal status,
item/sensitive-review counts and a short evidence-digest prefix. It never
offers an execute or approve button.

## Regression tests

Real PostgreSQL 17 + production ASP.NET/JWT tests verify:

- 605 review items over 600 refs (with one raw ACL and Graph group), 100-row
  keyset pagination, digest presence, and **no** target action.
- Concurrent API replicas converge on one pair and one audited queue entry;
  `SKIP LOCKED` grants exactly one live lease.
- Transient retry/backoff and terminal errors use safe codes. Old/expired
  lease tokens fail and cannot publish.
- A newer source snapshot blocks a queued old preparation; journal
  corruption cannot produce a partially committed review set.
- Zero-delta snapshots produce a valid SHA-256 of an empty stream rather
  than an invented deletion. Wrong destination and pagination are rejected.
- End-to-end JWT: anonymous/user blocked; admin POST idempotent; items
  concealed while queued, readable when Prepared; provider stub logs
  demonstrate no Azure DevOps or GitHub requests.

All F-001–F-025 tests remain. `main` stays untouched while stacked
pull requests are reviewed in order.

## Next implementation boundary

F-027 should introduce an **explicit operator review/approval** model,
including a last-minute source/target preflight and an auditable, separate
non-mutating dry-run. A later feature with its own security and integration
tests must be required before granting any GitHub write capability. Scheduled
Azure DevOps polling, production schema migrations and high-volume load tests
are not claimed by F-026.
