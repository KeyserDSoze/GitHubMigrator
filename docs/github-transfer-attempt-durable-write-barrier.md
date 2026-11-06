# F-047 — durable GitHub transfer preparation ledger & hard write barrier

**Delivered:** PostgreSQL-backed, two-person-reviewed preparation attempts and
a background worker that always stops **before any remote Git transport**.

**Not delivered:** permission to push, a GitHub App push token, a target-wide
write lock, ref creation, object pack transfer or remote object verification.
The code can only create non-executable records. A dry-run acknowledgement is
**not** a production execution approval. No refspec, path, token or Git command
is stored or accepted from the client.

## API, disabled by default

Enable \`GitHubTransferAttempts:Enabled=true\` only to exercise the dry-run
preparation journal. Its default is **false**. \`Jobs:Enabled=false\` also
disables the background worker (useful for integration tests). The ASP.NET
routes all require the password-changed JWT policy and superadmin role, with
a separate 6/minute limiter.

Root: \`/api/incremental-preparations/{preparationId}/operation-plans/{planId}/github-app-scopes/{scopeId}\`.

- \`POST .../transfer-attempts\`
  body \`{"expectedPacketSha256":"<F-044 digest>",
  "expectedPolicySha256":"<F-046 digest>",
  "leaseToken":"<F-045 lease UUID>","leaseGeneration":1}\`.
  The system **recomputes F-046 using GET-only GitHub policy calls**,
  rehashes and rechecks F-044 after the GETs, then validates a still-held
  F-045 PostgreSQL lease matching exact repository ID, packet digest, token,
  generation, actor and target identity.
- \`POST .../transfer-attempts/{attemptId}/review\` accepts only
  \`{"decision":"acknowledge-dry-run"}\` or \`{"decision":"reject"}\`.
  The reviewing superadmin **must be different** from the requester,
  and the original 5–20-second F-045 preview lease must still be valid.
  An acknowledgement changes the state to \`Reviewed\`, **not**
  \`ExecutionAuthorized\`.
- \`GET .../transfer-attempts/{attemptId}\` and
  \`GET .../transfer-attempts/{attemptId}/events\`
  provide read-only status/audit with no lease token returned.

The first background worker is explicitly constrained to
\`IGitHubTransferAttemptStore.BlockNextAtWriteBarrierAsync\`. It finds
\`Reviewed\` records and atomically transitions them to
\`BlockedAtWriteBarrier\` exactly once, appending the
\`WriteBarrierBlocked\` event *only* from the successful PostgreSQL
compare-and-swap update. The worker does not inject a GitHub API client,
credential minting service, Git process or remote transport.

## Fail-closed invariants

- Every attempt is bound to one source prep/plan/scope, numeric target repo,
  the exact F-044 packet SHA and candidate SHA, the F-046 policy SHA,
  a still-held F-045 actor/token/generation and a fresh observed policy.
- F-046 must report \`AdvisoryNoRulesReported\` for each candidate, with
  **zero** active rules or unknown statuses and at most 16 refs.
  Absence of reported rules remains **not proof of push permission**.
- PostgreSQL uses the **same transaction-scoped target advisory lock** as
  F-045 when preparing or human-reviewing attempts. A unique partial index
  prevents multiple active preparation attempts for one numeric repository.
  A separate unique scope index gives idempotency and blocks SHA conflicts.
- Database \`CHECK\` constraints permanently enforce:
  \`transport_attempted=false\`,
  \`execution_authorized=false\`,
  \`remote_writes_performed=false\`,
  \`ready_for_execution=false\`.
  They also enforce valid states, independent reviewer and terminal blocked
  timestamps. No code path ever sets one of these flags to true.
- The only state machine is
  \`AwaitingIndependentReview → Reviewed → BlockedAtWriteBarrier\`
  (or \`AwaitingIndependentReview → Rejected\`).
  There is **no transition out of the write barrier**.
- No timeout is converted into a GitHub retry. F-046's pure
  \`GitHubRemoteWriteOutcomeGuard\` already requires independently checked
  remote ref state after any ambiguous future transport outcome.

## Architecture boundary for the next increment

F-047 lays the durable control plane. A future **separate versioned
feature** must introduce an isolated, explicitly sandbox-allowlisted Git
object transport with fresh single-repository GitHub App credentials,
target-wide *write* coordination (distinct from the read-only F-045 lease),
strict compare-and-create refspecs, persisted ambiguous outcomes,
reconciliation after uncertain sends and exact remote post-write verification.
A test repository must be designated and approved before any real remote
mutation. Nothing in F-047 allows such an operation.

## Validation

CI covers PostgreSQL 17 cross-replica prepare replay, target exclusion,
distinct-human acknowledgement, review conflicts, worker CAS producing one
barrier event, expired preview leases, terminal rejection, SQL denial of
all four transport/execution flags, JWT/RBAC/feature gate and domain SHA
lineage rejection. No external GitHub writes are involved.
