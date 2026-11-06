# F-039 — Non-authorizing GitHub import-readiness evidence

## What has been implemented

F-039 adds the first **destination-side import review** after the F-032
source-vs-target ref comparison, F-034 durable workpack, F-035 Git bundle
export, F-037 source queue and F-038 live on-disk bundle integrity checks.

F-039 does **not** create a repository, push Git objects, grant a GitHub
permission, approve a migration or unlock any F-029/F-034 workpack. A
positive GitHub `permissions.push` value is an advisory metadata signal,
NOT a verified token Contents:write scope or bypass of rulesets, branch
protection, signed commit requirements or organizational controls. The
GET repository API requires only Metadata:read; it cannot establish
effective Git push capability.

## Admin API

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/import-readiness
Content-Type: application/json
Authorization: Bearer <admin-access-token>

{"expectedReconciliationSha256":"<64 lowercase hex>",
 "expectedBundleSha256":"<64 lowercase hex>"}
```

Authenticated admin/superadmin (and completed password change) only.
The endpoint is rate-limited; the caller cannot supply GitHub URLs,
credentials, repository names, filesystem paths, refspecs or push
instructions. Plan/mapping/target organization come from the immutable
F-029/F-032 source-scoped proof, and the PAT comes from the existing
encrypted secrets store.

```text
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/import-readiness
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/import-readiness/{reviewId}
```

The list returns 20 most recent immutable historical reviews. A
historical proof can become `isCurrent=false` if a later review is
captured or source/target evidence becomes stale. GET reads do not
claim to rehash disk bytes; the POST checks physical SHA-256 both
before and after its external GET.

## Verification sequence

1. Reject invalid GUIDs/SHAs before touching provider credentials.
2. Verify current F-029 operation plan, F-032 reconciliation, and
   blocked/non-executable F-034 workpack, including exact repository
   mapping, target observation digest and source reference count.
3. Fetch F-035 immutable source bundle proof, check SHA/bytes and
   **rehash the actual file on disk** via F-038.
4. Re-validate the GitHub destination connection and retrieve its PAT
   only server side. The GET-only fixed-host adapter requests
   `https://api.github.com/repos/{organization}/{repository}`. It
   checks HTTP status, body size <= 64 KiB, exact `full_name` and
   `owner.login`, `archived`, `disabled`, and optionally
   `permissions.push`. Missing, ambiguous, forbidden, unavailable,
   rate-limited or malformed replies remain **blocked**, never
   interpreted as permission.
5. PostgreSQL takes the existing source-scoped advisory transaction
   lock and rechecks F-029/F-032/F-034/F-035 and the GitHub connection
   validation timestamp. It re-hashes bundle bytes again. An
   intervening change rejects the new proof.
6. Write a SHA-256-sealed, immutable `github_import_readiness` row
   including plan/workpack/reconciliation/target SHA, bundle digest,
   categorized GET response and ref conflict counts. Unchanged replay
   is idempotent under a unique `(workpack_id, evidence_sha256)`
   constraint.
7. Return explicit blockers and **false**
   `writeCapabilityVerified`/`readyForExecution`, enforced by
   PostgreSQL CHECK constraints even after EF `EnsureCreated`.

## Blockers

Even when GitHub reports a visible, enabled/unarchived repo and
`permissions.push=true`, the mandatory blockers remain:
`ContentsWriteScopeNotVerified`,
`BranchRulesNotVerified`,
`GitHubImportExecutorNotImplemented`,
`WriteAuthorizationNotGranted`.

Additional blockers apply to unavailable/hidden repository, lack of
reported push capability, archived/disabled state, F-032 divergent
refs and target-only refs (which are preserved and never deleted).

No force-push, ref deletion or branch rewrite is allowed in F-039.

## Testing

Unit tests exercise GitHub GET-only calls, false/absent reported push
permission, invalid owner/name, 404 vs 403 vs rate limits, bounded and
malformed JSON, and SHA immutability/blocker derivation.

The real PostgreSQL 17 + ASP.NET integration verifies JWT admin
enforcement, refusal of synthetic database-only bundles before provider
GET, two-replica idempotent immutable evidence, source-scoped proof
mismatch rejection, SQL denial of both executable state and false
permission attestation, secret-free API reads and review history.
The PostgreSQL evidence test uses an **explicit test-only disk verifier**
for a synthetic bundle; the actual F-038 real-bundle SHA rehashing is
independently tested against on-disk Git artifacts and always used in
production dependency injection.

## Still missing

A production-safe write-capability probe cannot be inferred from
`GET /repos` alone. Before actual GitHub import, design a separate,
least-privilege write credential and explicit actor approval,
repository/ruleset policy attestation, destination ref lease,
non-destructive object upload, independently verified post-write refs
and rollback/recovery. GitHub Enterprise Server and on-prem TFS target
transports are not yet enabled.

Up-to-date GitHub API reference:
https://docs.github.com/en/rest/repos/repos#get-a-repository
