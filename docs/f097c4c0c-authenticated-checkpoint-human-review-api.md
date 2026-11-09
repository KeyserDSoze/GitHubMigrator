# F-097c.4c.0c — Authenticated checkpoint human-review API (incremental)

Tracked by [#108](https://github.com/KeyserDSoze/GitHubMigrator/issues/108)
and blocking [#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105).
This slice exposes only the internal [#107](https://github.com/KeyserDSoze/GitHubMigrator/issues/107)
append-only independent-human journal: it does **not** authorize destination reads.

## Authentication and routes

All three routes require the real `PasswordChanged` JWT policy,
the `superadmin` role and the existing staging-sync rate limiter.
The API is **off by default**. Full verified review requires three
independent explicit opt-ins:
`GitCheckpointTargetHumanReview:Enabled=true`,
`GitCheckpointTargetReReview:Enabled=true`, and
`GitHubSourceCheckpointGitRefLedger:Enabled=true`.
The existing `GitHubSourceSyncCheckpoints:Enabled` prerequisite also
remains mandatory. Enabling only the HTTP route never bypasses the
source-evidence gates.

Base path:
`/api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/target-human-review`

- `GET /candidate?sourceRepositoryId=...` loads the source-locked
  independently verified latest F-091/F-097b checkpoint and historical
  F-029 mapping, exposing only safe IDs, target name, expected
  `evidenceSha256` and permanently false authority flags.
- `POST /` accepts only `sourceRepositoryId`, `decision` and
  `expectedCandidateEvidenceSha256`. Allowed decisions are exactly
  `ApprovedForObservation` or `Rejected`. The reviewer UUID is
  exclusively derived from the authenticated JWT identity claim and
  the original requester from the persisted preparation, **never**
  from request-controlled identity fields. It rechecks the context,
  fingerprint, independent reviewer and current source evidence under
  PostgreSQL source advisory lock and atomically appends the receipt
  and audit event. Replays are safe; conflicting decisions are refused.
- `GET /?sourceRepositoryId=...` returns historical, immutable
  checkpoint-specific review and verifies the decision SHA-256, the
  exact database row and corresponding immutable audit event.
  Historical readback does not require declaring F-029 current.

Neither the preview nor the review readback exposes source Git refs,
OIDs, credentials, secrets, object paths, Git URLs or outbound HTTP.
An approval does **not** authorize GitHub provider GET, F-031, F-032,
F-038, destination push, retry, dispatch or migration completion.

## Read-only operator inspection

The source-checkpoint history displays a collapsed, superadmin-only
`SourceCheckpointHumanReview` widget for each source repository in
an F-091 v2 later checkpoint (not a baseline checkpoint). The panel
performs **GET only**, on explicit user interaction:

1. Try the exact scoped immutable decision/audit readback.
2. If there is no decision, preview the current verified candidate
   using `GET /candidate`; a superseded/revoked candidate remains blocked.
3. Check that all five authority flags are `false`, SHA-256s have valid
   lowercase shape, the checkpoint/snapshot/plan/repository mapping
   matches what is displayed, and the two reviewer/requester IDs are
   different for a historical decision. A server payload inventing
   execution/observation authority is rejected before display.

The web UI offers **no** decision POST, provider GET, credential
entry, Git URL, remote write, or execution/retry action. It is an
inspection aid only. The independently authenticated approval action
is intentionally kept in the separate backend API.

## Tested boundaries

- SHA-1 and SHA-256 domain/integrity tests reject decision tampering,
  forged authority bits and changed reviewer/candidate SHA.
- PostgreSQL tests create two real immutable F-014/F-091/F-097b
  source epochs. The second, not the stale F-029 baseline, is the
  only candidate. The authenticated HTTP test logs in as a real
  superadmin (JWT), previews the candidate, rejects stale SHA and
  foreign repository, submits approval for SHA-1 or rejection for
  SHA-256, proves claimant spoofing has no effect, replays exactly,
  refuses a conflicting decision, verifies scoped historical GET
  after F-027 revocation, and confirms no F-031/F-032 inserts or
  outbound GitHub transport.
- Existing real PostgreSQL journal tests cover four
  SHA-1/SHA-256 approval/rejection combinations, restart and
  concurrent replay, immutability of both row and event, expired
  validations, old-review revocation and verified historical
  readback. HTTP negative tests cover 401/403 roles, default
  disabled 409 and opt-in missing/invalid checkpoint 400/404/409.
- Playwright tests verify GET-only audit inspection, candidate-only
  fallback and rejection of a response claiming forbidden authority.
  The frontend is not the trust boundary; the PostgreSQL journal
  verifies the evidence and event.

## What remains outside this issue

Parent #105 still requires separate checkpoint-bound provider-read
authorization and an actual fresh, immutable F-031 target observation
for a later checkpoint, under independent source/review revalidation.
Only after that can #104/#103 use current F-032/F-038/F-035 workpacks
and #100/#96/#92 integrate physical mirror ancestry.

All writes in this issue target the PostgreSQL decision journal only;
all five destination authority flags stay false. Changes are directly
on sole `main`, with no feature branches or PRs.
