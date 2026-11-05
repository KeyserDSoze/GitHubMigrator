# F-032 — Immutable source/target Git-ref reconciliation

## Goal and status

F-032 connects the **historical, snapshot-bound Azure DevOps source evidence**
from F-014 and the **independently captured, GET-only GitHub target ref
observations** from F-031 through F-030's fail-closed Git ref diff engine.
It writes a versioned, immutable **comparison**, not an executable migration.
The compare POST has **no external provider requests** and no PAT access.
No Git history is transferred and **no target write or permission grant exists**.

The API exposes **one repository mapping per request**. A current F-029
operation manifest and a current F-031 target observation with the exact
SHA-256 digests are mandatory. Only known `ReviewTargetName` GitMirror
mappings are accepted; the F-029 steps remain `Blocked`.
A historical comparison remains readable after newer source or target evidence
supersedes it, but has `isCurrent=false`. Even an all-equal comparison
always has `readyForExecution=false`.

## New API

All routes require password-changed platform admin/superadmin; the compare
POST uses the existing `incremental-review` rate limiter.

```http
POST /api/incremental-preparations/{preparationId}/operation-plans/{planId}/ref-reconciliations
Content-Type: application/json

{
  "expectedManifestSha256": "64 lowercase hex chars",
  "sourceRepositoryId": "azure-git-repo-id",
  "targetObservationId": "uuid-of-existing-F-031-observation",
  "expectedTargetEvidenceSha256": "64 lowercase hex chars"
}
```

`GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/ref-reconciliations`
lists at most 20 immutable summaries.
`GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/ref-reconciliations/{reconciliationId}?after=refs/heads/main&take=50`
returns up to 100 ordered per-ref results and a keyset continuation token.
A valid comparison carries the snapshot ID, source and destination SHA proofs,
target observation ID, plan digest, counts by decision and a deterministic
SHA-256 over every comparison item.

## How source evidence is verified

Never read `staging_assets` for a historical comparison: it is a mutable
**latest canonical catalog**, and a newer scan can replace earlier refs.

- **Durable source scan with F-014 externalized refs:** read only
  `migration_discoveries` for the exact F-029 snapshot (without the inline
  ref array), then locate its completed `DiscoverSource` job and sealed
  F-014 manifest. Demand exact snapshot, source connection and total ref
  count. Walk its ordered 250-ref chunks through the existing immutable,
  SHA-verifying chunk reader and validate every chunk boundary, count and
  root SHA-256 before trusting *any* selected repository ref. Stream only
  a selected repository's bounded ref collection to the F-030 compare core;
  the source may have other repositories with many refs.
- **Legacy inline source scan:** only a globally **complete** source inventory
  can prove absence of a ref. PostgreSQL extracts only the selected repository
  ref name/OID pairs from its original JSONB snapshot, bounded by 25,001 rows.
  Partial inline scans fail with `SourceRefCoverageUnverified`. No historical
  F-014 manifest root is claimed for such snapshots.

Every selected source ref must be a Git-valid fully-qualified branch/tag ref,
contain a 40- or 64-hex Git OID and obey the 25,000 per-side limit. The
`verified-source-git-refs-v1` SHA binds snapshot ID, connection ID, source
repo ID, the real F-014 root (or explicit `legacy-inline` marker), sorted
full names and normalized OIDs. Mixed SHA-1/SHA-256 formats, case aliases
and malformed OIDs fail during F-030 reconciliation.

## Transactional safety and PostgreSQL

`incremental_git_ref_reconciliations` is an additive phase-0 table.
It is FK-bound to an F-029 plan and the F-031 target observation. The unique
`(plan_id, source_repository_id, target_observation_id,
source_evidence_sha256)` key makes unchanged replays idempotent; a new
observation SHA creates a new historical comparison, never overwriting
previously sealed evidence. The database CHECK **prohibits**
`ready_for_execution=true`.

Creation acquires the existing **source-scoped PostgreSQL advisory transaction
lock**, re-verifies the latest F-029 plan, source snapshot and mapped target
name, verifies F-031 JSON and SHA and its freshness (including credential
validation), reads the exact historical source refs, builds the deterministic
F-030 decisions, and commits the record while still holding the lock.
No external GitHub or Azure requests occur inside the transaction.

Read APIs recompute the complete F-030 digest by reconstructing source and
target sets from the persisted comparison rows. They detect changed ref
names, OIDs, dispositions, blocker codes, counters or proof digests.
The F-031 observation remains independently integrity checked. A verified
F-014 root is bound into the source-proof digest; a changed manifest root
makes an older comparison noncurrent.

| F-030 decision | Meaning | Execution |
| --- | --- | --- |
| `MatchingObjectId` | Both refs share name and Git object ID | Still not authorized |
| `CreateCandidate` | Source ref not in saved target observation | Blocked: write permissions unverified |
| `DivergedBlocked` | Same ref name, different OID | Blocked: fast-forward unverified |
| `TargetOnlyPreserved` | Target ref absent from certified source | Preserved; never inferred deletion |

Even two matching refs do not establish actual object reachability, Git
history completeness, GitHub write scopes or a completed migration.

## Tests and remaining work

- .NET/xUnit source provenance hash, canonical sorting, tampering and
  invalid ref guards.
- ASP.NET/JWT + real PostgreSQL17 integration extends F-028–F-031 with
  complete inline-source evidence, matching and target-only decisions,
  stale-plan fencing, precise authorization, replay/idempotence,
  keyset pages, counter tampering and **zero additional provider calls**.
- Existing F-014 chunk integrity and F-030 Git diff tests continue running
  in CI with React/Playwright and Terraform validation.

F-032 intentionally does not claim a verified true live Git repository
state or a functioning synchronization executor: source and target are
snapshots taken at different instants. Before any push, implement independent
write-authority proof, real Git mirror transfer with lease fencing,
retry/checkpoint/rollback governance, live post-write ref/object verification,
user-facing execution approval and production-reviewed schema migrations.
The React workbench wiring for viewing these comparisons is a follow-on
delivery, not claimed as included in F-032.
