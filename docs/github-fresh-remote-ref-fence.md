# F-042 — Fresh remote GitHub ref fence (GET-only)

## What F-042 actually establishes

F-041 proved with **real Git** that a verified F-035 bundle can be imported
into a disposable local bare destination. F-042 adds the last observational
precondition needed before a future remote executor: **fresh remote-ref
drift detection**. It does not write to GitHub or authorize a push.

An F-042 remote fence ties exactly these already sealed proofs together:

- F-029 operation manifest and destination organization;
- F-031 exact, immutable target refs (including annotated tag object type);
- F-032 source-vs-target reconciliation and immutable target evidence SHA;
- F-035 and F-038 complete physical bundle SHA-256;
- F-040 acknowledged, unexpired two-person Git create-only intent;
- F-041 successful real-Git local rehearsal receipt.

The operator may POST only a F-041 receipt SHA, not remote URLs, credentials,
paths, desired ref names, pushes, or Git commands. A server-side verified
connection is used to perform **two fixed-host GitHub REST GETs**:
`/repos/{org}/{repo}/git/matching-refs/heads` and `tags`.
The existing F-031 transport enforces HTTP status, fixed host, ref caps,
bounded payloads, no redirects and exact annotated tag object IDs.
Not found or forbidden NEVER becomes an empty target.

## Endpoints (admin/superadmin JWT)

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/git-rehearsals/{rehearsalId}/remote-fences
{"expectedRehearsalEvidenceSha256":"<64 lowercase hex>"}
```

```text
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/remote-fences/{fenceId}
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/git-rehearsals/{rehearsalId}/remote-fences
```

The POST is independently rate-limited. A recent snapshot (at most
30 seconds old) produces `RefSetUnchanged` or
`RemoteDriftBlocked`. The operation never updates F-031, because
creating a *new* F-031 observation would invalidate the original F-032
reconciliation, F-039 readiness, F-040 intent and F-041 rehearsal.
Instead the new complete remote evidence is SHA-256 sealed in its own
append-only F-042 table.

## Comparison semantics

The source of truth is the **complete** F-031 target ref page chain.
A fresh GitHub snapshot is compared by full ref name, object ID
and object type. Any new ref, deleted ref, changed SHA, or change
from an annotated tag object to a commit type becomes
`RemoteDriftBlocked`, even if count is identical.

Two hashes are recorded: the F-031-compatible fresh observation SHA
(which also binds observation timestamp) and a deterministic SHA
of just the ordered name/OID/type set, which is stable across repeated
observations of unchanged refs. Mismatches are counted; branch names
and raw ref payloads are not exposed in the response.

## PostgreSQL and concurrency

Under the same source-scoped advisory lock as F-029–F-041, the
F-042 store rechecks every dependency and **rehashes the full
physical bundle again** after the GitHub API GET. It saves an immutable,
uniquely indexed `(rehearsal_id, remote_evidence_sha256)` row,
with a 30-second expiry for the observational evidence.
Repeated identical sealed observations are idempotent; history is
bounded to the most recent 20. Historical GET never implies a new
network check or physical disk read.

SQL CHECK, also applied when EF `EnsureCreated()` already created
the table, requires:

```text
remote_writes_performed=false
write_capability_verified=false
execution_authorized=false
ready_for_execution=false
```

These flags are **permanent** in F-042. A matching remote ref set
does not prove a Contents:write token, Git push credential, bypass
of branch/ruleset protection, nor a remote atomic reservation.

## What F-042 does not promise

GitHub refs can change a microsecond after the REST GET. A future
write executor MUST still carry an object-ID compare-and-create lease
on *each* remote ref, use independent scoped short-lived credentials,
check GitHub policy and proof again inside the worker, verify all
post-write objects and handle partial transfer/restart. A snapshot
with `isCurrent=true` is NOT executable authorization.

No GitHub push, deletion, overwrite or protected branch update
is possible through any F-042 endpoint. The only live provider
methods remain GET. No second credential or permission to write
is installed or reused by this increment.

Tests cover exact SHA/object type matching and drift, bogus proofs,
JWT, PostgreSQL idempotency, blocked flags and the real-F-041
rehearsal prerequisite. Production GitHub write credentials remain
a separately reviewed future feature.
