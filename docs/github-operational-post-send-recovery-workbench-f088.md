# F-088 — operational remote recovery workbench with durable PostgreSQL timeline

## Purpose

F-082 pessimistically commits a *possible* GitHub transmission, never
a successful migration. F-086 stores the original typed ref/tag OIDs;
F-087 can independently obtain authenticated F-054 tips and F-058 full
object closure; F-085 can record a further independent checkpoint.

F-088 brings that evidence to a **superadmin-only operational page**.
It is not a migration execution view. It never treats GitHub GET
success as proof that the sender caused a remote change, and never
turns any historic evidence into push authority.

## Dashboard

A new **Remote recovery / Recupero remoto** navigation item allows
superadmins to select a valid incremental preparation UUID, choose an
immutable operation plan and inspect the latest post-send epochs for
its explicitly allowlisted GitHub sandbox repository.

Each row presents:

- GitHub organization/repository and numeric repository ID
- F-082 possible-send timestamp and SHA-256 evidence
- F-086 typed original ref snapshot SHA, ref and candidate counts,
  *only when the sealed historical F-086 snapshot is available*
- F-054 remote tip readback SHA (or pending)
- F-058 independently audited full Git object closure SHA (or missing)
- F-085 independent checkpoint SHA (or missing)
- Human-readable recovery state and the only sensible next action

State is projected exclusively from verified PostgreSQL journals:

| State | Suggested next step |
| --- | --- |
| `AwaitingIndependentReadback` | F-087 GET-only observe-tips when F-086 is available |
| `OperatorReviewRequired` | Manual investigation; no automated continuation |
| `TipsObservedNeedsClosure` | F-087 independent object verification |
| `IndependentClosureObserved` | F-085 further independent human checkpoint |
| `CheckpointedIndependentClosure` | None; still evidence only, never completed |

If F-086 cannot be loaded with valid audit and F-082 provenance,
the workbench does **not** assume an empty baseline. It marks the
epoch for manual review instead of offering remote reads.

Every row and every page has `CanPush=false`, `CanRetry=false`,
`CanDispatch=false`, `MigrationComplete=false`. The UI fails
closed if a server response ever asserts otherwise.

## API and performance

`GET /api/incremental-preparations/{id}/operation-plans/{planId}/remote-recovery-workbench?take=12&after={barrierId}`

- JWT password changed + superadmin authorization, rate-limited
- Disabled by default: `GitHubRemoteRecoveryWorkbench:Enabled=false`
- Exact numeric `AllowedRepositoryId` and `AllowedRepository=org/repo`
  must match the separate F-082 allowlist. The F-086 allowlist is
  independently enforced for typed baseline display.
- Keyset order by immutable F-082 PostgreSQL record time and GUID.
  `take` is strictly 1–20, no unbounded `OFFSET`.
- Uses already-persisted F-082+F-052 records to locate epochs for the
  chosen preparation and plan; every returned epoch is independently
  verified by F-085's F-082/F-054/F-058/F-085 audits and F-086's
  SHA-sealed typed historical snapshot. Corrupt history returns an
  error, not a partial success row.
- Returns SHA digests, IDs, counts, states and timestamps only. No
  private key, token, Git URL, raw ref names, raw Git objects or
  source bundle is serialized.
- A non-existent/cross-plan cursor is rejected. Limit+1 detects the
  next cursor without guessing whether more rows exist.

The workbench is a **read-only GET**. There is no HTTP POST route to
the workbench itself. The three explicitly confirmed, human-initiated
actions reuse existing independently guarded endpoints:

- F-087 POST `.../independent-recovery/{barrierId}/observe-tips`:
  GitHub App **Contents:read** + GitHub GET only
- F-087 POST `.../independent-recovery/{barrierId}/verify-full-objects`:
  GitHub App **Contents:read** + Git smart upload-pack/fetch, F-056/F-058
- F-085 POST `.../post-send-recovery/{barrierId}/closure-checkpoints`:
  creates immutable certification **only** for already-persisted proof

All three actions require a deliberate browser confirmation, are
separately role-gated and independently disabled by default. The
interface never invokes F-083, a Git push, a replay or a background
send queue. Two distinct readers and one additional independent
certifier are required by the server-side lineage/actor guards.

## Verification

- .NET domain tests enforce bounded, safe state→next-action mappings,
  forbid all push/retry/dispatch/completion flags and reject forged
  F-058/F-085 proof.
- Real PostgreSQL 17 tests create the original F-081→F-086→F-082
  chain, read the workbench after a new connection/crash, register
  F-054, persist F-058, checkpoint F-085, and verify the projected
  state after another restart. Test cursor ordering, foreign-plan
  scoping, page-size limits and invalid cursor rejection.
- SQL integrity test appends a duplicate F-054 event; F-085 verifies
  event uniqueness, so a corrupt history cannot look successful.
- TestServer JWT/RBAC tests cover anonymous/user/admin denial,
  disabled-by-default superadmin behavior and absence of any workbench
  POST mutation route.
- Playwright tests cover the real UI with mocked server evidence:
  pagination, warning badges, explicit read-only actions and no-body
  POST paths, checkpoint progression, never retry, unauthorized roles,
  HTTP integrity error display and rejection of forged retry flags.
- Italian and English translation keys, responsive layout.

## Remaining boundaries

This is monitoring of a strictly sandbox-scoped experiment, not a
general migration deployment claim. F-083 live sender is still
internal-only and disabled by default. CI never performs a live push
to GitHub.com or mints a real installation token. Migration completion
and production rollout must be governed by a separate verified
business acceptance and operator workflow. `main` stays unchanged;
this PR is stacked on F-087.
