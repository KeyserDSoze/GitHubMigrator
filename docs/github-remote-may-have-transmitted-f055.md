# F-055 — Persistent MayHaveTransmitted safety journal (NO remote sender)

The last completed increment F-054 stores immutable, provider-sealed read-only
GitHub ref and object-tip evidence. That evidence is not an outbound
transport receipt. F-055 introduces a **separate, default-disabled**
PostgreSQL protocol that a future *independently approved* remote Git sender
would need to call **before any network push**.

## State machine

- `PreparedWriteDisabled`: a short-lived internal, SHA-bound preparation
  recorded only while F-047 is BlockedAtWriteBarrier, F-050 is
  ReviewedWriteDisabled, and F-052 is ReviewedWriteDisabled for the same
  numeric repository, fence request, generation and immutable packet/policy
  metadata. A fifth independent actor is required. None of those states
  authorizes remote Git writes.
- `MayHaveTransmitted`: an **irreversible, pessimistic safety marker**
  committed with an audit event before any potential transport by a future
  sender. It is NOT evidence that Git was actually invoked; in F-055 there
  is explicitly **no Git transport implementation at all**. It cannot be
  aborted or re-queued. Even after process crash, a different replica must
  respect the persistent possible-send marker and reconcile through a
  separate independent observation/review workflow.
- `AbortedBeforeTransport`: possible only from a fresh prepared state,
  with the original actor and a one-time cancellation token. Does not
  consume any possibility of remote execution: there was no transport.
- `OperatorReviewRequired`: expired **prepared** state is quarantined
  by a transactionally persisted audit event. It cannot be automatically
  recovered or used to mint another attempt. An already
  MayHaveTransmitted record is NEVER auto-expired or cleared.

The transition to `MayHaveTransmitted` is not a write grant; it is
a conservative *uncertainty record* that will block a later retry. F-055
cannot initiate a push or claim that GitHub has received any data.

## Cross-replica and safety controls

- Database rows bind F-052 grant, F-050 request/generation, physical
  packet SHA-256, candidate set SHA-256 and original baseline SHA-256.
  One consumed grant per record and at most one active hazard per
  **numeric GitHub repository**. An aborted epoch cannot be reused.
- Every mutation holds the same PostgreSQL advisory transaction lock as
  F-050/F-052/F-054. The actual F-047/F-050/F-052 rows are reloaded
  under that lock for prepare and possible-send mark.
- Marking requires original preparer + one-time UUID abort token. The
  secret is kept only in the immediate response, with a domain-separated
  SHA-256 digest in PostgreSQL. **No credential, remote Git URL, refspec
  or private key is accepted or stored.**
- SQL CHECK constraints forever force `transport_authorized=false`,
  `transport_invoked=false`, `remote_writes_performed=false` and
  `may_automatically_retry=false`. A database trigger forbids mutation
  of the immutable identity/proof columns, deletion and transitions out
  of a terminal hazard. Audit events reject UPDATE/DELETE.
- Config is independent of F-052:
  `GitHubRemoteTransmitHazard:Enabled=false` (default),
  `AllowedRepositoryId` (numeric) and
  `AllowedRepository` (org/repo). The internal journal has no API
  endpoint and no background sender, network client or Git runner.
- F-052's 20-second observed deadline is retained: a real sender must
  obtain a new complete source and remote verification plus independent
  approved authority. This increment does NOT lift it.

## Crash semantics

1. A future sender would first commit an immutable **prepared** record.
2. It would then irrevocably commit **MayHaveTransmitted before I/O**.
3. Crash/lost ACK after step 2 must be classified as possibly sent,
   even when no ref changes are observed. Blind retry is forbidden.
4. Only a future **separate, authorized** reconciliation path can
   adjudicate the remote ref set *and complete Git object history*;
   neither F-053 tip readback nor F-054 historical receipt alone is a
   success signal. No automatic reset exists in F-055.

## Verification

- xUnit domain tests: separation from the six earlier actors,
  original policy/packet binding, token-domain separation, stale and
  revoked F-047/F-050/F-052 evidence.
- Real PostgreSQL 17: six competing replicas select one preparer,
  mark-vs-abort races select one terminal outcome, recovery after restart,
  expiry quarantine, append-only audit, irreversible possible-send state,
  and direct SQL flag/proof tampering rejection.
- CI retains React/Playwright and Terraform checks.

**Not implemented:** remote GitHub write transport, user-facing approve/send
API, GitHub object closure or post-send independent reconciliation. No
verified remote migration is claimed, and `main` is untouched.
