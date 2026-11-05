# F-028 — Live GitHub destination preflight (GET-only, no execution permit)

F-028 extends F-027 independent planning review by taking **bounded, live,
read-only GitHub observations** after a superadmin has approved the plan for
planning. It distinguishes visible target repository name collisions, ambiguous
404 responses, missing/unknown organization membership and provider failures.

This is an observation step, **not execution authorization**. The flags
`repositoryCreatePermissionVerified`, `targetAvailabilityVerified` and
`readyForExecution` remain **false** unconditionally: neither a GET 404 nor
membership policy proves write capability. No GitHub create/delete/permission
API or Git migration executor is introduced.

## Preconditions and scope

`POST /api/incremental-preparations/{id}/target-preflight` requires an
authenticated admin/superadmin and a prepared F-026 job that has a
**current F-027 `ApprovedForPlanning` review**. The caller sends the
review SHA-256 as an optimistic-lock proof and selects **1–10 Git source
repositories**, mapping each to a GitHub target name and explicit history
strategy.

Repository identity, project membership and target naming are validated using
the existing F-005 `MigrationPlanReviewService.PreviewAsync` from the
immutable discovery snapshot. Duplicate targets (case-insensitive), unknown
source IDs, invalid names and incompatible TFVC strategies reject before any
GitHub requests. This preflight does not remove the F-005 effective-permissions
blocker and does not independently certify history completeness.

The destination is already verified as GitHub Enterprise Cloud. Its
authenticated login comes from stored F-002 validation, not request input.
PATs are read through the configured secret store and passed only to the
read-only GitHub adapters. No token appears in persisted observations, API
responses, HTTP logs or an audit event.

## What the live reads actually establish

The existing fixed-host `GitHubDestinationAccessReader` performs:

- `GET https://api.github.com/user/memberships/orgs/{org}` with strict
  matching organization and authenticated user login.
- `GET https://api.github.com/orgs/{org}` for advisory organization policy.

For each selected target, the existing
`GitHubTargetRepositoryProbe` performs one
`GET https://api.github.com/repos/{org}/{repo}` with an 8-second
per-request deadline. All adapters reject redirects and cookies and bound
JSON response sizes. All unknown HTTP results, malformed JSON and timeouts
fail closed.

Supported sanitized repository statuses: `Exists`,
`NotVisibleOrMissing`, `Unauthorized`, `Forbidden`, `RateLimited`,
`InvalidResponse`, `Unavailable`. Provider response bodies are **not**
stored. A 200 response with the matching `full_name` proves a visible
collision and sets `state=Blocked`. A 404 is ambiguous (a private/hidden
repository can also return 404); with no visible blockers the state is only
`Unverified`, never `Ready`.

## PostgreSQL durability and race fencing

The new tables are:

- `incremental_target_preflights`: one latest observation per preparation,
  source/destination validated-at timestamps, F-027 review SHA, observation
  SHA, bounded repository/collision/uncertain counts, membership role/status,
  advisory organization policy, timestamp and increasing revision.
- `incremental_target_preflight_probes`: primary key
  `(preparation_id,source_repository_id)`, selected target name, strategy,
  sanitized GET result. Maximum ten rows per preflight.
- `incremental_target_preflight_events`: append-only actor/time/evidence
  SHA/status/revision for changed preflight observations.

The checked-at timestamp is rounded to milliseconds **before hashing** to
match PostgreSQL `timestamptz` precision and make read-back integrity checks
stable across database persistence.

The versioned SHA-256 preimage binds the F-027 review digest, job/snapshot/
source/destination IDs, organization, connection validation timestamps, the
observation time, organization membership/policy, all selected source-to-
target mappings and all individual GET classifications. Snapshot and
preflight rows are rechecked on every GET; a changed persisted status,
count, mapping or digest causes `TargetPreflightIntegrityFailed`.

**External HTTP happens before the database transaction.** On persistence,
the existing per-source advisory lock (shared with F-013/F-026/F-027) is
acquired and the backend independently rechecks the prepared job, approved
review digest, latest source snapshot, verified source/destination connection
types and exact validation timestamps. A superseding scan, revoked review or
rotated/revalidated connection **during** the remote checks cannot publish
an apparently current preflight. Older observations cannot replace newer
ones. Re-probing genuinely re-issues GETs; an identical observation SHA
is idempotent and creates no extra event.

An observation's `isCurrent` is a **short-lived database-freshness label**:
it requires the latest snapshot, current planning review and unchanged
validation metadata **and** a checked-at timestamp within five minutes.
It is not proof that GitHub state or permissions remain unchanged.

All tables are added to phase-0 PostgreSQL bootstrap, not a production-reviewed
schema migration. No reusable execution grant or writable outbox is created.

## API and UI

All routes inherit `PasswordChanged` and admin/superadmin RBAC. Live POST
uses the existing `external` rate limiter.

| Route | Purpose |
| --- | --- |
| `POST /api/incremental-preparations/{id}/target-preflight` | Read-only, bounded live GitHub preflight; body `{expectedReviewSha256,repositories:[{sourceRepositoryId,targetRepositoryName,strategy,include:true}]}`. |
| `GET /api/incremental-preparations/{id}/target-preflight` | Last persisted bounded observations with integrity/freshness check. |
| `GET /api/incremental-preparations/{id}/target-preflight/events` | Up to 100 audit revisions. |

The React Discovery workbench allows editing/selecting up to 10 source-to-
target names after a current `ApprovedForPlanning`, and shows membership,
visible collisions, unknown availability, the evidence SHA and freshness.
All labels are available in Italian and English. No button can execute or
approve writes to GitHub.

## Regression tests

Real PostgreSQL 17 integration: 200/404 distinction; active membership with
all 404s remains Unverified; unapproved/revoked reviews cause **zero external
requests**; exact proof/revisions and idempotent replay; source snapshot and
destination validation changing during a GET cause atomic rejection and no
row; persisted same-count tampering is detected; provider target names,
source identities and 10-repository limit validated before GETs.

Production ASP.NET/JWT + encrypted credential store + strict fake provider
transport: anonymous/user rejected, admin approval prerequisite, 4 actual
GitHub GETs (membership, org, two repositories), 200/404 classification,
authorization scheme/host validation, zero target writes, no PAT in response,
audit GET without provider calls, stale flag after destination revalidation.

Existing .NET/xUnit, PostgreSQL integration, React/Playwright and Terraform
CI gates remain mandatory.

## Explicit next steps

F-028 is not a Git push/mirror, production run scheduler, creation-scope
attestation, enterprise permission resolver, effective-rights interpretation
or execution authorization. A future *separate* execution gate must obtain
fresh live target state, documented write authorization, a binding to an
approved immutable operation plan, audit/retry controls and a fully tested
rollback design. Nothing in F-028 shortcuts those requirements.
