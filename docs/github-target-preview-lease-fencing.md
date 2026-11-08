# F-045 — PostgreSQL target-wide preview lease & generation fencing (NO REMOTE WRITES)

F-045 introduces **one read-only preparation lease per immutable GitHub
numeric repository ID**, shared by all API instances. The lease is *not*
a GitHub ref lock, transport lock or authorization to push. It coordinates
operators preparing the same destination and records immutable audit events.

This is a prerequisite, not the future remote Git transfer worker.

## Enabled only by explicit configuration

\`GitHubTargetPreviewLeases:Enabled=true\` (default **false**).

All routes require password-changed JWT + **superadmin** role and an independent
6-per-minute request limiter. An active GitHub App scope and fresh F-044
packet are derived server-side for every acquisition, renewal or recovery.
Neither client-provided refs, repositories, Git commands, credentials nor
filesystem paths are accepted.

Root route:

\`/api/incremental-preparations/{preparationId}/operation-plans/{planId}/github-app-scopes/{scopeId}\`

- \`POST .../target-preview-leases\` body \`{"durationSeconds":15}\`.
  Returns opaque lease token, generation and exact packet SHA-256. Short
  valid duration: 5–20 seconds. A different replica receives \`409 PreviewLeaseBusy\`.
- \`POST .../target-preview-leases/{leaseToken}/renew\` body
  \`{"generation":1,"expectedPacketSha256":"<SHA>","durationSeconds":10}\`.
  Requires the *same* still-fresh F-044 packet and current owner/generation.
- \`POST .../target-preview-leases/{leaseToken}/release\` body
  \`{"generation":1,"expectedPacketSha256":"<SHA>"}\`.
  Releases only a live matching lease owned by that JWT subject.
  A stale actor, old token, stale generation or expired lease cannot release.
- \`GET .../target-preview-lease\` returns a redacted view of the
  repository-level lease without the opaque token.
- \`GET .../target-preview-lease/events\` returns the latest 100 append-only
  events (\`Acquired\`, \`Renewed\`, \`Released\`, \`Quarantined\`, \`Recovered\`).
- \`POST .../target-preview-leases/recover\` body
  \`{"expectedGeneration":1,"oldPacketSha256":"<SHA>",
  "acknowledgementText":"I CONFIRM F045 PERFORMED NO REMOTE WRITES AND THIS IS NOT PUSH AUTHORIZATION"}\`.
  Requires a **different human** from the original lease owner and a **new
  F-044 packet**, derived from a fresh F-042 remote observation and F-043
  issuance attestation. A new generation invalidates every old token.

## Concurrency and crash guarantees

- PostgreSQL transaction-scoped advisory lock on the **numeric GitHub repo
  ID**, including the first insert before a row exists. All instances,
  regardless of organization slug/case, contend for the same target.
- Atomic write predicates compare token + generation + actor + packet
  digest + not-expired lease. Old worker/checkpoint attempts cannot produce
  a success event. Every event is derived only from a successful mutation.
- An expired \`Held\` lease is **never auto-reassigned**. It is marked
  \`RecoveryRequired\` and quarantined. Recovery uses an explicit, independently
  acknowledged review and a new packet; it does *not* infer that a remote Git
  push succeeded or failed.
- A new claim after release or recovery receives a new UUID and strictly
  higher generation. Different numeric repository IDs do not interfere.

The persistent table's SQL \`CHECK\` enforces
\`execution_authorized=false\`, \`remote_writes_performed=false\` and
\`ready_for_execution=false\`. There is **no GitHub token, GitHub API
mutation, git process, write queue or live remote transport** in F-045.

Before any real push: implement independent run authorization, effective
per-ref ruleset checks, short-lived GitHub App token *inside* a remote
worker, durable ambiguous-transfer reconciliation, compare-and-create,
source object transfer, and exact remote SHA verification. **These preview
leases cannot be promoted to production write authorization.**

## Testing

PostgreSQL integration runs concurrent claims from independent DbContexts
as if they were separate replicas; tests verify one winner, release and
renew CAS, stale-token rejection, isolation by numeric target, strict expiry
quarantine, separate-human fresh-evidence recovery, append-only audit,
feature-disabled API, and SQL denial of all three write/execution flags.
