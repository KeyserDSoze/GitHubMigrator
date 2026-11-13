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

## Phase 2: independently authorized fixed-host GitHub GET and F-031

The next explicit opt-in `GitCheckpointTargetObservation:Enabled=true`
is independent from `GitCheckpointTargetReadPreflight:Enabled=true`.
The new `POST /api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{checkpointId}/target-ref-observations`
requires the same `PasswordChanged` JWT and `superadmin` role.
It accepts only the source repository ID and expected candidate/human
decision SHA-256 values. **The body cannot select a GitHub URL, OID,
credential, organization or reviewer.** The principal ID comes from JWT.
The endpoint is rate-limited by the existing external-work limiter.

When both feature switches and all current #106/#108 checks pass,
the service resolves the destination's validated connection and
encrypted server-owned credential, then invokes the existing
fixed-host `GitHubRefObservationReader`. It can issue **only two GETs**
to `https://api.github.com/repos/{organization}/{repo}/git/matching-refs/heads`
and `.../tags` (no redirects); its transport rejects inaccessible,
hidden, throttled, partial, oversized, invalid or mixed-format ref
responses. The response must contain 1..128 canonical refs, whose Git
object IDs all match the source's SHA1/SHA256 object format. No HEAD,
POST, PATCH, PUT, DELETE or push adapter is injected into the service.

After the provider GET, the service independently reruns the source
and review preflight. It then acquires source FIRST and plan SECOND
PostgreSQL advisory transaction locks and rechecks newest staging,
checkpoint, reviewed mapping, active F-027, exact human review event,
destination organization/provider/validation and expiry before
atomically storing a NEW checkpoint-specific F-031 receipt and
`ObservedReadOnly` audit event. The immutable evidence SHA-256 includes
the human decision, candidate and preflight SHA-256, checkpoint ID,
source snapshot, target ref hash, Git object format and observation time.
Historical F-029/F-031 rows are untouched. Exact-scope idempotent
replays return verified existing evidence without another provider GET;
a changed target observation cannot overwrite an existing receipt.

The additive `git_checkpoint_target_observations` and
`git_checkpoint_target_observation_events` tables have SQL CHECK,
UNIQUE, FK and UPDATE/DELETE-blocking triggers. The scoped GET endpoint
verifies the stored full receipt digest, canonical Git ref hash and
exactly one matching immutable event before returning a **redacted**
summary. Neither receipt nor API response grants push, retry, dispatch,
additional provider GET or migration-completed authority. The frontend
provides only explicit GET-only saved-history inspection after a
human-approved checkpoint; it never triggers the provider POST.

## Still open in #109

Complete the two-epoch and provider failure/concurrency/restart/
tamper test matrix and require green CI 4/4 before closing #109.
The saved ref-name presence is not a proof of Git object ancestry or
permission to write. Workpack F-032/F-038/F-035 integration still belongs
to #104/#103, and physical mirror ancestry remains #100/#96/#92.
Parent #105 stays open pending the integrated checkpoint-scoped workpack.
No push, retry, dispatch or migration success is authorized.
