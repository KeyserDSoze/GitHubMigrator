# F-050 — dedicated remote GitHub sandbox write fence (NO GitHub pushes)

F-050 adds the PostgreSQL control-plane primitive required **before**
considering any future GitHub remote transfer. It is NOT an execution grant,
remote Git transport, token minting mechanism, or completed migration.

Existing F-045 preview leases are **read-only 5–20 second** coordination
for preparatory evidence. They are not reused as remote write locks.
This F-050 fence is independently keyed by GitHub's **numeric global
repository ID**, with monotonic generations and durable quarantine.

## Enablement

Disabled by default:

\`\`\`text
GitHubRemoteSandboxFence:Enabled=false
GitHubRemoteSandboxFence:AllowedRepositoryId=<numeric GitHub repository ID>
GitHubRemoteSandboxFence:AllowedRepository=<org>/<sandbox repo>
\`\`\`

Enabling this flag activates only PostgreSQL coordination, not a push.
No GitHub API client, Git process, credential provider or token minting
service is injected into any F-050 endpoint/worker.

A single explicit **repository name plus its numeric ID** must match
the full F-044 source/target/physical-bundle evidence and an existing
independently reviewed, F-047 \`BlockedAtWriteBarrier\` attempt.
Attempt and packet must also match F-044 exact ref candidate hash and
F-047 policy SHA. Every proposal/review/recovery re-derives fresh F-044
evidence from server-controlled reconciliation and physically rehashes
the source bundle; user-provided packet JSON is never accepted.

## Security boundaries

The dedicated PostgreSQL table records one **current fence row per
globally identified GitHub repository**. A short-term proposal holds
the row, but has no remote transport permission:

- **Proposed** — proposal from a superadmin with fresh evidence;
  response includes an opaque **random fence token** that is stored in
  PostgreSQL only as a domain-separated SHA-256 digest. Review must
  occur within 25 seconds, subject to the stricter F-042/F-044
  freshness windows.
- **ReviewedWriteDisabled** — a *different* superadmin revalidates
  current F-044 evidence and independently reviews. The coordination
  deadline is extended by 20 seconds. **The state does not authorize
  a GitHub push.** The F-047 barrier stays closed.
- **Released** — only the original proposer can release using
  request ID, generation and opaque token, **before expiry**.
- **RecoveryRequired** — if either of the prior stages expires,
  the row is atomically quarantined (with exactly one durable audit
  event) and **cannot be silently reacquired**, even when the
  old token is available.
- **RecoveredNoWrites** — a different superadmin from the original
  requester *and* reviewer must supply fresh valid F-044 evidence and
  the exact explicit F-050 no-transport attestation. This is possible
  only because F-050 physically cannot send GitHub writes; future
  remote executors will need a stronger independent reconciliation
  protocol instead of this no-write recovery.

After Released or RecoveredNoWrites, a new proposal increments the
monotonic generation. Stale request IDs, generation numbers, tokens,
reviewers, wrong scope, wrong packet SHA and wrong target IDs cannot
extend or release a newer fence. All state changes use repository-
scoped PostgreSQL transaction advisory locks and CAS conditions.
Concurrent replicas cannot acquire two fences for one repository.

PostgreSQL **CHECK constraints permanently enforce**
\`execution_authorized=false\`,
\`remote_writes_performed=false\` and
\`ready_for_execution=false\`, in *every state*. The fence has no
method to transition into a writable state.

## API

JWT roles: superadmin, password changed; separate 5/minute limiter.

Base:
\`/api/incremental-preparations/{prep}/operation-plans/{plan}/github-app-scopes/{scope}/remote-sandbox-write-fence\`

- \`POST /propose\`:
  \`{"attemptId":"<F-047 ID>","expectedPacketSha256":"<F-044 SHA>"}\`
  returns a one-time claim with token.
- \`POST /{requestId}/review\`:
  \`{"attemptId":"<F-047 ID>","generation":1,"expectedPacketSha256":"<F-044 SHA>"}\`
  returns **status without token**.
- \`POST /{requestId}/release\`:
  \`{"attemptId":"<F-047 ID>","generation":1,"fenceToken":"<claim UUID>"}\`
  returns \`releasedWithoutRemoteWrites=true\`.
- \`POST /recover\`:
  \`{"attemptId":"<fresh F-047 ID>","oldRequestId":"<UUID>",
  "generation":1,"expectedPacketSha256":"<fresh F-044 SHA>",
  "acknowledgementText":"I CONFIRM F050 HAS NO REMOTE TRANSPORT AND REQUEST MANUAL FENCE RECOVERY"}\`.
- \`GET /\` returns redacted status **without token/hash**.
- \`GET /events\` returns up to 100 immutable audit events.

Quarantine worker is disabled if F-050 is disabled, or if
\`Jobs:Enabled=false\`. Even without worker polling, acquisition,
review and release enforce deadline and quarantine under the same
transaction. Quarantine commits **even when a rejected operation
returns 409**, avoiding silent rollback of expiry.

## Coverage

Real PostgreSQL 17 multi-replica competing proposals and single
global repository row; two-person review; stale review; expiry
quarantine; blocked reacquisition; manual different-actor recovery;
monotonic epochs; stale token/actor/generation release refusals;
wrong repository allowlist; SQL attempts to enable any remote-write
flag; ASP.NET JWT/RBAC feature-off test.

**Next**: F-051 can construct a separate independently authorized,
strictly test-repository-only GitHub remote Git adapter with a token
never persisted, pre/post effective ref observation, no blind retry
and independent remote object verification. No such adapter or
execution authority is shipped in F-050.
