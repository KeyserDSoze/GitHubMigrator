# F-049 — PostgreSQL-ledger local Git dispatch and inspection-only crash recovery

F-049 bridges the **actual local Git create-only transport** (F-048)
to durable PostgreSQL dispatch and the existing F-047 two-person
\`BlockedAtWriteBarrier\` control-plane state.

**Scope:** Only operator-preprovisioned local bare Git repositories
addressed by server-side UUID allowlist. **No GitHub remote writes,
GitHub tokens, remote URL or GitHub execution authority.** The original
F-047 hard remote-write barrier is neither modified nor lifted.

## Feature gating

F-049 is disabled unless all of the following are set by the server
operator:

\`\`\`text
LocalGitSandboxDispatch:Enabled=true
LocalGitSandboxDispatch:AllowedSandboxIds=uuid-1,uuid-2
LocalGitSandboxDispatch:DestinationRoot=/absolute/trusted/local/bare/root
LocalGitSandboxDispatch:ScratchRoot=/absolute/trusted/private/scratch
GitSourceBundles:BundleRoot=/absolute/trusted/bundles
Jobs:Enabled=true  # background processing only; API queue remains gated
\`\`\`

A malformed or absent UUID never enters the allowlist. The bundle is
hashed from disk and the destination folder is derived exclusively as
\`<DestinationRoot>/<sandbox UUID in N format>.git\` by F-048.
There is no API for configuring paths, Git commands, refspecs or
credentials.

## Superadmin-only API

All require a password-changed superadmin JWT with a separate
6/minute limiter. Root:

\`/api/incremental-preparations/{prep}/operation-plans/{plan}/github-app-scopes/{scope}/transfer-attempts/{attempt}/local-sandbox\`

- \`POST\` body \`{"sandboxId":"<allowlisted UUID>"}\`. It checks the
  F-047 **separately reviewed, write-barrier-blocked** attempt; *rebuilds*
  F-044 with current proof and double-verified physical bundle; obtains
  complete F-032 reconciliation pages (max 512 source/target refs)
  and matches the exact F-044 CREATE-only SHA; persists a job bound to
  the numeric GitHub destination ID and original packet evidence.
  Actual execution remains only against the *local* allowlisted sandbox.
- \`GET\` returns limited job status without the internal claim token,
  Git refs, filesystem paths, Git arguments or source payload.
- \`GET /events\` returns append-only state events.
- The API cannot inspect arbitrary attempts: all routes verify the
  prep/plan/scope/attempt relationship before accessing job metadata.

## State machine — no retries after possible send

\`\`\`text
Queued
  |  PostgreSQL conditional claim + PotentialSend audit, COMMIT before Git
  v
MayHaveTransmitted ---------- successful exact verified Git ----------> Verified
  | error, lost ACK or cancellation
  v
InspectionRequired
  | database conditional claim
  v
Inspecting ------ full refs + git fsck ------> ReconciledMatch
  |                                   or
  +-------------------------------------> NeedsOperator

MayHaveTransmitted/Inspecting + expired 10-minute claim
  -> Inspecting ONLY (never back to Queued, never re-push)
\`\`\`

The *intent to transmit* is committed to PostgreSQL **before** F-048
starts Git. The worker can die at any point without leaving an
eligible second send; the job becomes inspection-only once the
claim window expires. Even a lost acknowledgement after a completely
successful atomic Git push is recovered by independent F-048
\`InspectAsync\`, including \`git fsck --full --strict\`; this results
in \`ReconciledMatch\`, **not** a fabricated successful transport
receipt. If target refs do not match or inspection fails, the state
is \`NeedsOperator\`. A new transfer would require a *new*, separately
reviewed control-plane attempt and fresh F-044 evidence.

PostgreSQL serializes admission with F-045/F-047's global numeric
repository advisory transaction lock, unique attempt replay,
unique active sandbox and unique active global repository ID indexes.
Worker claim/inspect/complete all compare state plus fresh claim UUID,
and append an audit event only for successful CAS updates.
The F-048 on-disk per-target in-process/POSIX locks still prevent
overlap against the same physical bare target.

SQL CHECKs permanently enforce all of:
\`remote_writes_performed=false\`,
\`execution_authorized=false\`,
\`ready_for_execution=false\`.
Even \`Verified\` means **local sandbox success only**.

## Validation

Real PostgreSQL 17 + real Linux Git integration tests validate
multi-replica idempotent enqueue and single admission, no auto retry,
CAS rejection of stale worker tokens, inspection after a crash,
SQL refusal of remote-write flags, allowlist denial, end-to-end real
bundle → PostgreSQL queued job → local bare target → post-write verified
receipt and simulated lost acknowledgment recovered by a separately
claimed read-only inspection. JWT integration covers role gating.
No write to any GitHub repository is made.

**Next:** separate remote execution authorization + scoped GitHub App
credential, durable target-wide *write* fencing, and remote OID readback
must be introduced in a future feature. F-049 is not that feature.
