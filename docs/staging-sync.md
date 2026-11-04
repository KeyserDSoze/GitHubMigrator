# F-013 — Normalized intermediate staging and repeatable source synchronization

**Status:** first working, read-only-to-providers ingestion slice. Every successful F-003 discovery is still saved as an immutable snapshot; it is then normalized into indexed PostgreSQL asset records. Operators can retry normalization of older unsynced snapshots without repeating Azure DevOps reads. This implements part of the staging layer in the AGIC *Product Capabilities & Technical Architecture* (pp. 3, 5 and 21).

## Flow

1. F-003 obtains Azure DevOps projects, repositories, Git refs, identity and security evidence using a transient credential.
2. The full original `migration_discoveries` JSONB snapshot is immutable.
3. The `StagingAssetNormalizer` produces stable source identifiers and SHA-256 fingerprints of allowlisted metadata.
4. `PostgresStagingRepository` performs one **transactional, serialized** upsert per source connection using bounded JSONB batches of 250 assets. It records first seen / last seen / last changed snapshots, a revision number and missing status.
5. `staging_snapshot_syncs` records the snapshot's observed, added, changed, unchanged, missing and coverage counts. Replaying the same snapshot returns its original result without writes.
6. A later snapshot updates only changed fingerprints. Older snapshots are rejected (HTTP 409) instead of regressing the catalog. Missing assets are only marked for categories whose discovery coverage is explicitly complete.
7. The React discovery screen displays synchronization counts and provides an explicit *Sync selected snapshot to staging* recovery operation.

## Normalized asset kinds (first slice)

| Type | Canonical key | Change fingerprint content |
| --- | --- | --- |
| `project` | source project provider ID | project name |
| `repository` | source repository provider ID | name, version-control kind and TFVC path |
| `ref` | JSON pair of repository ID and ref name | target object SHA |
| `identity` | source Graph identity ID | display name, email and legacy descriptor |
| `group` | provider group descriptor | display name and legacy descriptor |
| `membership` | JSON pair of group and member descriptor | source group/member edge |
| `acl` | JSON tuple of repo ID, security token, principal and scope | raw allow/deny masks and inheritance flag |

Source ACL records remain **raw evidence**, not effective GitHub grants. No token, credential reference, large Git pack, LFS payload or attachment body is included in normalized asset payloads. The `ref` content hash is based on the object ID, not bytes of the repository.

## Coverage guarantees

- Absent `project`, `repository` or `ref` entries are marked missing **only** after a `DiscoveryCompleteness.Complete` snapshot.
- Missing `identity` rows require `IdentitiesComplete=true`.
- Missing `group` or `membership` rows require their individual complete flags.
- Missing `acl` rows require both `PermissionsComplete=true` and `AclsCollected=true`.
- Staging's `sourceInventoryComplete` is conservative and is only true if complete source, identity and security coverage, including effective permissions, is explicitly attested. It must **not** be interpreted as migration/cutover readiness.
- A partially collected asset remains visible with its previous observed evidence. Its absence is an unknown, not a delete instruction.

## API (admin/superadmin, completed password change)

- `POST /api/discoveries`: still performs discovery. It now automatically syncs staging before acknowledging success. If staging fails after the immutable snapshot was persisted, the snapshot can be retried using the next route.
- `POST /api/staging/snapshots/{snapshotId}/sync`: idempotent catalog ingestion/replay; `409 StagingSnapshotOutOfOrder` if an older snapshot was never applied after a newer one.
- `GET /api/staging/snapshots/{snapshotId}`: persisted sync counts; 404 if not yet applied.
- `GET /api/staging/connections/{sourceConnectionId}/summary`: latest synced snapshot, active/missing asset counts and total revisions.
- `GET /api/staging/connections/{sourceConnectionId}/assets/{assetType}?take=50&after={lastSourceId}`: bounded keyset-paginated catalog access (`take` between 1 and 100). `items` includes source identity, fingerprint, revision, missing flag, provenance snapshot IDs and structured metadata; `nextAfter` is the next-page cursor or null. Asset types: `project`, `repository`, `ref`, `identity`, `group`, `membership`, `acl`. The composite primary key provides indexed access; callers do not have to load all source assets in memory. The view is the **current** catalog, not a historical/approved snapshot isolation view while future scans run.


The ingest route has a separate rate limit. No endpoint sends **writes** to Azure DevOps or GitHub, and these staging routes do not send external provider requests at all.

## Scalability and current limitations

**Implemented:** indexed PostgreSQL keys; batched SQL (250 rows per command, not per-object EF saves); compare-by-hash updates; transactional catalog/sync ledger; indexed keyset pages; per-connection PostgreSQL transaction advisory locks; bounded snapshot size; recovery and out-of-order protection; tests with 1,200+ source refs.

**Not implemented yet:** true provider-side delta scanning or continuous live synchronization, generic asset pagination, PostgreSQL production EF migrations replacing development bootstrap DDL, an outbox/queue, dependency-graph edges and materialized cross-asset mappings, Blob-backed immutable Git packs and LFS, imported pipelines/Boards as first-class staged objects, retention/compaction, benchmarked throughput/latency SLOs, tenant-level data isolation, write execution or automatic cutover.

The F-003 reader is still a **full, synchronous discovery** with an HTTP deadline. The new incremental layer avoids unnecessary *staging revisions*, not source API calls. Before declaring enterprise performance, add server-side source pagination and cursors, durable workers (F-008), streaming artifact storage, backpressure/parallelism, indexes measured with `EXPLAIN ANALYZE`, and load tests for 10k–1m assets and interrupted runs.

## Verification

- Pure normalizer tests: stable fingerprints, changed ref objects, duplicate keys, large in-memory sets and conservative coverage.
- Real PostgreSQL integration: 1,200-ref batched ingest and replay, delta detection, partial-scan safety, stale snapshot rejection and source isolation.
- API integration: JWT admin RBAC, POST replay, GET summary, no external provider requests.
- Playwright: operator recovery workflow and honest partial coverage reporting.

Staging status is **not** an approval or a successful migration.
