# F-085 — durable independent post-send recovery from F-082, F-054 and F-058

## Motivation

A successful or failed F-083 Git push can be ambiguous after a network
timeout, process crash, lost acknowledgement or interrupted HTTP readback.
F-082 already commits the pessimistic **MayHaveTransmitted** hazard before
any possible send, but operators need to see whether **independent,
authenticated remote evidence** has arrived after that marker.

F-085 **does not reinterpret the sender's in-process result as remote
success**. It binds existing append-only PostgreSQL evidence to the exact
reviewed preparation/plan/scope, creates a fresh *read-only recovery
projection* and lets a new independent operator checkpoint the complete
evidence only after F-054 and F-058 have both been persisted.

## Read projection, including after a crash

`PostgresGitHubRemotePostSendRecoveryJournal.InspectScopedAsync`
requires the original F-082 barrier, F-081 confirmation, F-080 intent
and reviewed plan/scope. It reruns F-082's independent hash, audit and
F-055 MayHaveTransmitted checks, then examines:

- **No F-054 readback:** `AwaitingIndependentReadback`. This is the
  normal state if the process crashed after F-082, even if the actual
  push never started. The process must NOT retry the send.
- **F-054 recorded mismatch:** `OperatorReviewRequired`. A conflicting
  remote ref set cannot be silently reported as a completed migration.
- **F-054 exact authenticated ref/tip observation, but no F-058 closure:**
  `TipsObservedNeedsClosure`. Commit tips are NOT a full-object proof.
- **F-058 independent complete commit/tree/blob/tag closure and F-054:**
  `IndependentClosureObserved`. The F-058 canonical SHA-256,
  event/observer/time and all F-054/F-082 lineage hashes are checked.
- **New independent operator writes F-085 checkpoint:**
  `CheckpointedIndependentClosure`. This makes the already-observed
  evidence independently attestable after another restart; it does not
  mean the sender definitively caused those refs to appear.

Every state and the final checkpoint permanently has
`CanRetry=false`, `CanDispatch=false`, `MigrationComplete=false`.
Neither the F-058 proof nor a journal checkpoint authorizes an
unattended job or a remote Git operation.

## Scope and immutability

F-085 adds append-only tables
`github_remote_post_send_closure_checkpoints` and
`github_remote_post_send_closure_events`. One checkpoint is allowed
per F-082 barrier, F-055 hazard, F-052 grant, F-054 readback and F-058
reconciliation; unique constraints, foreign keys, SQL CHECKs and
UPDATE/DELETE-rejecting triggers protect the evidence. It has no tokens,
Git URL/refspecs, passwords, SSH keys or raw Git objects. Only IDs,
hashes, state and time are retained. The SHA-256 canonical checkpoint
binds all predecessor IDs and hashes and the independent operator.

Under both existing repository advisory locks **732911 → 733050**,
`RecordIndependentClosureAsync` reopens the F-082/F-054/F-058
evidence, verifies F-058's own original canonical hash, verifies unique
audits and requires a human different from the previous sender marker,
reviewers and independent F-054/F-058 observers. The readback must be
timestamped **after F-082**, and F-058 after the F-054 observation.
Duplicate same-operator calls return the same immutable receipt; a
different operator cannot overwrite it.

An independent full-closure checkpoint is intentionally unavailable
before F-058. A crashed worker never fabricates an outbound
acknowledgement or starts a retry.

## HTTP surface

These routes are **disabled by default** with the existing allowlist
and restricted to `PasswordChanged` + `superadmin`. They have the
same throttling policy as F-081 confirmations.

- `GET /api/incremental-preparations/{id}/operation-plans/{planId}/github-app-scopes/{scopeId}/remote-sandbox-review-sessions/{sessionId}/dispatch-intents/{intentId}/confirmations/{confirmationId}/post-send-recovery/{barrierId}` — read-only scoped state after a restart
- `POST <same>/closure-checkpoints` — **body-free**; records one
  independent checkpoint if a valid, already-persisted F-058 proof
  exists. It never contacts GitHub or launches Git.

No endpoint causes a GitHub push. The actual sender remains F-083,
internal-only and explicitly disabled by default.

## Tests

- End-to-end **real PostgreSQL 17** F-081→F-082 setup and simulated
  process restart, confirming the pessimistic pending state.
- Exact F-054 tips then F-058 closure then F-085 independent
  checkpoint, new connection readback and permanent no retry.
- Six concurrent replicas all receive the same immutable checkpoint,
  with exactly one checkpoint row and one audit event.
- No F-054 audit, corrupted F-058 closure digest, mismatched actor,
  F-054 operator-review state and cross-plan scope refuse to checkpoint.
- SQL mutation/deletion of evidence and events is rejected.
- .NET unit tests for all IDs/hashes/timestamps bound into F-085 digest,
  and for rehashing a persisted F-058 full closure.
- API TestServer JWT/RBAC and disabled-by-default tests; no external
  provider calls.

## Remaining operational milestone

A **real isolated GitHub.com sandbox** needs an explicitly approved
installation and ephemeral secret. The current CI does not write to
GitHub.com. F-084 already proved actual atomic create-only Git push
and independent F-057 full closure against Linux bare repositories in
SHA-1 and SHA-256. F-085 makes the independent post-send evidence
operationally inspectable and durable; it is **not** a claim that a
production live-GitHub end-to-end run has happened.

This PR stacks on F-084; `main` remains unchanged.
