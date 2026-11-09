# F-097c.4c.0d — checkpoint target-read preflight, phase 1

Tracked in [issue #109](https://github.com/KeyserDSoze/GitHubMigrator/issues/109)
under [parent #105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105).
Depends on completed #106 (source-only later-checkpoint rereview),
#107 (immutable independent human journal) and #108 (JWT review API).

## What this implementation does

A new **disabled-by-default, PostgreSQL-only** guard can produce a
short-lived SHA-256 readiness witness **without a provider-read grant**.
Its explicit setting is `GitCheckpointTargetReadPreflight:Enabled=true`.
The pre-existing independent switches for F-091 source checkpoints,
F-097b source ref proof, #106 rereview and #108 human review must also be
enabled; one switch never bypasses any other.

GET:
`/api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/target-read-preflight?sourceRepositoryId=...&expectedCandidateEvidenceSha256=...&expectedHumanReviewEvidenceSha256=...`

The endpoint requires a real JWT with `PasswordChanged` and the
`superadmin` role. It derives the actor from the JWT subject, not a
request-controlled reviewer UUID. The actor must exactly be the
**independent reviewer who approved this latest checkpoint**.

The guard re-reads #106 independently verified source ref/OID chain,
then reads the full SHA-256-verified #108 immutable decision and audit
event, requiring exactly `ApprovedForObservation`. It compares both
caller-provided SHA-256 handles to thwart optimistic-concurrency
switches. Inside source-first and plan-second PostgreSQL advisory locks,
it independently rechecks the latest staging snapshot and checkpoint,
source proof digest, reviewed baseline plan's immutable mapping,
original F-027 approval and actor, source and destination provider
validation timestamps, destination GitHub org and the unchanged
human decision row/event. Destination validation must still be fresh
(<5 minutes). Stale, rejected, superseded, tampered or foreign
checkpoints are denied without any network traffic.

It seals a deterministic `f097c4c0d-checkpoint-target-read-preflight-v1`
SHA-256 witness with preparation, plan, checkpoint, snapshot, target
mapping, source OID format/count, exact candidate digest, human review
digest and expiry. The API response omits requester/reviewer identifiers,
provider credentials, Git URLs and arbitrary refs/OIDs.

**Important: the witness is NOT an authorization to call GitHub.** It
always declares `providerReadAuthorized=false`, plus
`canObserveTarget=false`, `canPush=false`, `canRetry=false`,
`canDispatch=false`, and `migrationComplete=false`. It is not
persisted as F-031 and does not restore historical F-029 currency.

## Tests

- Domain SHA1/SHA256 seal tests for deterministic proofs, rejection,
  expiry limits and tampered digest/target/authority flags.
- Real two-epoch SHA1/SHA256 PostgreSQL tests validate rejection vs
  approval, identity binding, stale candidate/review handles, missing
  source repo and lack of any F-031/F-032 rows.
- Real JWT API tests check 401/403 role boundaries, default-off 409,
  malformed digest 400 and unknown checkpoint 409 without touching
  outbound provider transport.
- Positive JWT HTTP tests with real later F-014/F-091/F-097b checkpoints
  verify the non-authorizing witness only after #108 approval, reject
  alternate reviewers, stale digests, rejection and later revocation.

## Still open in #109

The **separate actual provider GET authorization and network operation**
must be built next: fixed-host GitHub Cloud GET-only access, verified
server credentials, rate limits, complete/ref format validation,
source-locked post-GET recheck, a brand-new immutable checkpoint-scoped
F-031 receipt + audit event and verified historical readback. No legacy
F-029/F-031 reuse. #105/#104/#103/#100/#96/#92 stay open. No push, retry,
dispatch, migration success or authorization to write a target.
