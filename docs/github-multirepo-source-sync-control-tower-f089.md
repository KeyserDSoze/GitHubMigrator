# F-089 — multi-repository incremental source-sync control tower

## Operator outcome

The platform's existing durable Azure DevOps/TFS discovery worker and
PostgreSQL staging journal are now visible **per repository** alongside
the F-088 GitHub recovery workbench. An operator can understand which
source refs changed in an immutable reviewed plan, whether a newer
*PostgreSQL-staged* snapshot exists, and whether the one explicitly
allowlisted GitHub test repository has an F-082 possible-send marker.

This is **NOT** automatic GitHub synchronization. Source staging,
local persistence, planned Git changes, remote GitHub transport,
post-send verification and business migration completion are distinct
operations and cannot be silently conflated.

## GET-only API and SQL performance

`GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/repository-sync-pulse`

- JWT PasswordChanged + superadmin + rate limiter.
- Disabled by default until
  `GitHubMultiRepositorySyncPulse:Enabled=true`.
- Verifies the exact F-029 immutable manifest and plan DAG, F-026
  preparation linkage and immutable F-025 staging change journal.
- Accepts only a preparation and plan GUID, **never arbitrary source
  repositories, target repository names, auth tokens, Git refs, or URLs**.
- F-029's maximum is ten mapped source repositories. Returns exactly
  one row for each source mapping with target name, source repository
  ID, blocked execution reason, count of Added/Changed/Restored/Missing
  refs and its plan-sealed total.
- For each mapping, a PostgreSQL aggregate counts F-026 preparation
  changes using `source_id::jsonb ->> 0 = :sourceRepositoryId`. That
  checks the exact first element of the canonical F-013 JSON ref
  natural key; it cannot accidentally include `repo-10` in
  `repo-1` or misinterpret `%` and `_` as SQL wildcards. No ref
  payloads/OIDs are loaded into application memory. The query count
  is bounded by ten mappings, independent of the ref count.
- The global source snapshot includes F-013 observed/added/changed/
  unchanged/marked-missing values, the F-025 immutable change
  summary, and an optional matching durable discovery/staging job
  state/error code. **Direct staging without a job** is displayed
  honestly, never fabricated as a successful background job.
- A second indexed PostgreSQL lookup checks the latest *committed*
  staging snapshot of the same source connection. If different, the
  plan is labelled `SupersededInPostgres`; this is historical, not a
  reason to auto-replay any GitHub push.
- Partial source inventory is explicitly marked. Even the latest
  committed source snapshot cannot prove that the live Azure DevOps
  source has no newer data; the response always has
  `ProviderSourceFreshnessVerified=false`.
- Only when the independently enabled F-088 allowlist matches the
  plan's **exact** organization/target name does the reader consult
  the already-audited single-repository F-088 timeline. It validates
  its numeric GitHub ID and full no-execution flags before surfacing
  latest status and a link. Unmonitored targets never receive a
  guessed numeric ID, success status or GitHub authority.
- No sender, GitHub App token or GitHub.com transport is injected into
  the F-089 reader. The response and each repository permanently
  have `CanPush=false`, `CanRetry=false`,
  `MigrationComplete=false`; the plan remains nonexecutable.

## React operator workflow

A separate **Source sync / Sincronizzazione sorgenti** navigation
item provides:

1. preparation UUID and immutable operation plan selection
2. snapshot provenance, staging timestamp, manifest SHA and the
   latest-committed/superseded status
3. job state, asset totals, partial-source warning and ref counts
4. filterable cards for up to ten source repositories, each showing
   added/changed/restored/missing counts and the target's blocked
   operation reason
5. a **single sandbox-only** link to F-088 recovery if an actual
   immutable F-082 epoch was recorded
6. an explicit, confirmed **Queue source refresh** action that
   reuses the existing `POST /api/jobs/discovery` endpoint with a
   fresh idempotency request UUID, plus a manual `GET /api/jobs/{id}`
   status check.

A source refresh performs Azure DevOps discovery and PostgreSQL
staging with the existing durable lease/idempotency worker. It may
have its own bounded job retries, but it NEVER invokes GitHub push or
replays F-082. It does not mutate the historical operation plan,
create a new approval, or automatically change the destination.
An operator must create/review a new plan from newer staging.

All UI content is bilingual (English/Italian), responsive, and
explicit about the difference between **locally staged** and
**migrated**. The client also fails closed if a response ever claims
provider source freshness, ready-for-execution, GitHub push,
automatic retry or migration completion.

## Verification

- xUnit domain tests for two-repository accounting, stale snapshots,
  forbidden GitHub send/retry/completion flags and forged sandbox
  metadata.
- Real PostgreSQL 17 integration reuses the *actual* F-013→F-025→
  F-026→F-027→F-028→F-029 seeded two-repository plan (including
  hundreds of source refs), checks one row per repo, exact JSON
  source ID equality, bounded counts, role/scope denial, staged source
  timestamp, and tampered F-029 digest rejection.
- A separate history fake verifies that only the *single exact*
  F-088-allowlisted target is inspected. Unmonitored repositories
  never call it; a forged GitHub numeric ID or retry flag fails
  closed.
- TestServer API JWT/RBAC checks anonymous/user/admin denial,
  disabled-by-default no-provider operation and absence of POST
  mutation on the sync pulse.
- Seven Playwright tests verify multi-repository counts/filtering,
  snapshot supersession, explicit single discovery job request,
  manual job status, single sandbox recovery link, unauthorized roles,
  and visible failures for forged retry flags/corrupt journals.

## Boundaries and rollout

F-089 does not widen F-082/F-083/F-087 GitHub transport allowlists to
additional repositories. It only widens **read-only source staging
visibility** to the ten repositories in one already-reviewed plan.
No real GitHub.com push is performed in CI, no production sender
endpoint is enabled, no GitHub synchronization loop is implied, and
no remote migration is marked completed. `main` remains untouched;
the branch is stacked on F-088.
