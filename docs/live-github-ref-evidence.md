# F-031 — Audited, GET-only GitHub target Git-ref observations

## What was added

F-031 is the next read-only vertical slice after F-029/F-030. The authorized
operator can collect GitHub Cloud **branch and tag Git-object references**
for a *specific* source-to-target mapping in an **integrity-verified,
current F-029 manifest**. It stores a bounded, immutable observation
in PostgreSQL and exposes summary + paginated ref reads.

This is **target-side evidence**, not yet a joint source/target comparison.
The source F-014 manifest reader and F-030 reconciliation core exist, but
are **not connected to this F-031 endpoint**. The next increment must join
those proven source observations to these persisted target snapshots,
persist each computed comparison, and expose results in the React workbench.
No GitHub writes, Git transfer, approval or successful sync is claimed.

## HTTP surface

All routes are JWT/password-rotation protected and require platform admin
or superadmin. POST has the existing external provider rate limiter.

- `POST /api/incremental-preparations/{preparationId}/operation-plans/{planId}/target-ref-evidence`
  with `{ "expectedManifestSha256": "<64 lowercase hex>", "sourceRepositoryId": "..." }`.
  The digest must match the current F-029 plan, and the source repository ID
  must occur in exactly one immutable `ReviewTargetName` plan node.
- `GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/target-ref-evidence`
  gives the latest 20 immutable observation summaries.
- `GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/target-ref-evidence/{observationId}?take=50&after=...`
  returns an SHA-verified, keyset-paged list of at most 100 refs.
  Neither endpoint returns PATs, raw GitHub responses or execution commands.

The provider reader uses only **two GETs** to
`https://api.github.com/repos/{org}/{repo}/git/matching-refs/heads`
and `.../git/matching-refs/tags`. These endpoints preserve
`object.sha` of **annotated tag objects**, unlike a listing of peeled
commit SHAs. The adapter pins the host, validates owner/repo segments,
sends no cookies, follows no redirects and enforces a 12-second HTTP client
timeout and a 20-second overall collection budget. The two JSON arrays
have separate 2 MB response caps and a combined 25,000-ref cap.
Unexpected `Link` pagination, 404 (hidden **or** missing), 403, 409,
malformed JSON, invalid ref names, case collisions, duplicate names and
bad OIDs **fail closed**. No missing-repository response is interpreted as
an empty ref inventory.

Observations are sanitized to `{Name,ObjectId,ObjectType}`. The digest
hashes a version tag, organization, repository, normalized timestamp,
count and every sorted ref/type/OID. The reader validates `commit` for
branches and `commit` or `tag` for tags, without peeling tags.

## Database and currency

`incremental_target_ref_evidence` uses a UUID primary key, FK to the
F-029 manifest, repository mapping ID, target name, exact manifest SHA,
observation SHA, source credential validation timestamp, collection
timestamp, bounded ref count, sanitized JSONB refs and actor ID. Unique
`(plan_id,source_repository_id,evidence_sha256)` ensures same-evidence
replays are idempotent; new proof appends history. It never updates older
observations. PostgreSQL CHECK prohibits `ready_for_execution = true`.

After both external GETs the repository obtains the existing
source-scoped advisory lock, independently verifies the **current**
F-029 operation manifest and mapping plus the GitHub destination's
validated-at timestamp, and commits only if the context still matches.
A superseded review, snapshot, preflight, manifest or revalidated PAT
cannot publish a current record.

Reads recompute the evidence SHA from persisted JSON and reject modified
counts, names, OIDs or object types. `isCurrent` additionally requires
the latest observation for that mapping, unchanged destination credential
validation, fresh F-029 proof and evidence age <=5 minutes.
Historical rows remain readable with `isCurrent=false`.
`readyForExecution` is **always false**.

## Tests and remaining limitations

.NET/xUnit tests exercise genuine HTTP adapter serialization and
fixed-host behavior, annotated tags, 404/403/429/409/302, invalid Git
ref names/types, size/paging limits, privacy and SHA stability.
The existing ASP.NET/JWT + PostgreSQL17 integration exercises live-read
authorization, non-executable persistence, paginated retrieval, stale
connection evidence and zero GitHub writes with a strict GET-only stub.

**No source-to-target reconciliation is claimed yet.** A GitHub GET
observation is not an atomic snapshot across both namespaces; changes
between requests can yield transient evidence. No write-scope proof,
repository creation, target ref update/deletion, transfer checkpoint,
rollback or production migration is implemented. F-031 uses additive
phase-0 schema bootstrap, **not production-reviewed database migrations**.
For large repositories, replace the bounded JSONB observation with
verified page/chunk-based storage and consistency fencing.

GitHub REST ref documentation:
https://docs.github.com/en/rest/git/refs#list-matching-references
