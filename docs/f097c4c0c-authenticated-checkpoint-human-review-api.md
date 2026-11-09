# F-097c.4c.0c — Authenticated checkpoint human-review API (incremental)

Tracked by [#108](https://github.com/KeyserDSoze/GitHubMigrator/issues/108)
and blocking [#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105).
This slice exposes only the internal [#107](https://github.com/KeyserDSoze/GitHubMigrator/issues/107)
append-only independent-human journal: it does **not** authorize destination reads.

## Authentication and routes

All three routes require the real `PasswordChanged` JWT policy,
the `superadmin` role and the existing staging-sync rate limiter.
The API is **off by default**; explicitly configure
`GitCheckpointTargetHumanReview:Enabled=true` to enable it.

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

## Test scope and remaining work

Domain tests cover historical self-contained digest and authority tampering.
API integration exercises 401/403 across roles, disabled-by-default
superadmin 409 and zero outbound provider requests. The existing real
PostgreSQL two-epoch tests validate the #107 journal. Full opt-in
HTTP positive/negative review and historical audit readback coverage,
restart/concurrency via authenticated requests and read-only
Playwright work remain required before closing #108.

Parent #105 still requires separate checkpoint-bound provider-read
authorization and actual fresh F-031 target observation. #104/#103/
#100/#96/#92 remain open. Only sole `main`; no feature branches or PRs.
