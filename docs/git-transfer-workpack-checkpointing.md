# F-034 — Durable Git transfer **preparation** workpacks

## Intent

F-034 is the first delivery in the future Git transfer execution track. It
adds a durable, crash-recoverable **evidence preparation** queue, not an
approved Git mirror executor. A workpack is tied to exactly one
integrity-verified F-032 Git-ref reconciliation, its SHA-256, the F-029
operation plan's manifest SHA and the current target observation.

There are no Git process invocations, provider calls, GitHub write scopes,
credentials, push commands or remote mutation paths in this feature.

## Workflow

`POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/ref-reconciliations/{reconciliationId}/transfer-workpacks`

Body:

```json
{"expectedManifestSha256":"<64 lowercase hex>", "expectedReconciliationSha256":"<64 lowercase hex>"}
```

Requires the platform admin/superadmin policy, the current F-029 plan
and current F-032 reconciliation, validated against their server-side
proofs within the existing source-scoped PostgreSQL advisory lock.
Repeated identical requests converge on one workpack by a unique
`reconciliation_id`, even across API replicas; no duplicate Queued
events are written.

`GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks`
returns at most 20 summaries. The same route with
`/{workpackId}` returns a status; `/{workpackId}/events` returns a
bounded append-only event history. All remain admin/JWT gated.

The durable phase machine is:

```text
Queued / EvidencePending
    | Claim: PostgreSQL FOR UPDATE SKIP LOCKED
    v
Running / EvidencePending
    | Re-validate exact F-032 & F-029 proofs under source advisory lock
    | CAS: lease token + attempt + unexpired lease
    v
Running / EvidenceChecked  <- crash recovery resumes HERE
    | Re-validate proof again, CAS
    v
Blocked / WriteBlocked [DestinationWritesNotAuthorized]
```

Only state transitions `Queued`, `Running`, `Blocked` and `Failed`
exist. **No success state and no approved execution.** PostgreSQL CHECK
constraints forbid `ready_for_execution=true` or an inconsistent
`Blocked` phase. There is no GitHub communication in the worker.

## Recovery & safety

Every worker takes a unique random lease token, with attempt counter and
time-limited ownership. PostgreSQL `SKIP LOCKED` prevents two replicas
claiming the same row. Each checkpoint and failure is an atomic
`UPDATE ... RETURNING` / append-only event INSERT conditioned on
lease identity, attempt and expiration. An old worker cannot checkpoint,
retry or finalize a newer attempt.

A dead worker leaves a lease to expire. The next worker **resumes from
the last durable phase**; it never resets `EvidenceChecked` to
`EvidencePending`. The retry policy has exponential backoff, maximum
five attempts, and a terminal failure if the final lease expires. Any
proof change causes a safe failure rather than a write-barrier bypass.

The processor sets a 45-second deadline within a two-minute lease.
A separate background worker can be disabled with `Jobs:Enabled=false`
or `GitTransferWorkpacks:Enabled=false` and can run across replicas.
It never logs raw exception bodies or provider secrets.

The queue stores only UUIDs, ref provenance SHA-256 digests, mapping
names, states, lease tokens and safe error codes. No PAT or raw source
data appears in the table or API response.

## Data and testing

`git_transfer_workpacks` and `git_transfer_workpack_events` are
additive phase-0 PostgreSQL tables bootstrapped after the existing
schema initializer. Production requires explicit reviewed EF migrations.

The integration suite runs the entire F-028→F-034 pipeline through
real ASP.NET/JWT and PostgreSQL 17, with the external provider reader
strictly GET-only. It verifies idempotency across two replicas, denial
on forged digests or unauthorized callers, an evidence checkpoint,
simulated worker crash/lease expiry, reclaim and resume by another
worker, stale-worker fencing, durable event order and a blocked terminal
result. No additional Azure or GitHub API call is issued by F-034.

**Not implemented:** Git pack download/upload, Git object mirroring,
credential isolation for a write-capable executor, independent
write-permission attestation, human write authorization, force-push
policy, GitHub ref update or post-transfer verification. These require
separate features before a real migration can run.
