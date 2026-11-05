# F-014 — Incremental, crash-safe Git ref observations in PostgreSQL

**Scope: first streaming ingestion slice for source Git refs.** The durable Azure DevOps discovery worker now stores normalized ref observations as **each provider ref page arrives**, rather than waiting for the entire source inventory before *any* asset data is queryable in the intermediate database.

This extends [F-008 page checkpoints](durable-discovery-pages.md) and [F-013 canonical staging](staging-sync.md).

## Flow and transaction boundary

1. A `DiscoverSource` job holds a PostgreSQL lease and issues fixed-host Azure DevOps GETs.
2. For each Git ref page (maximum 1,000 entries), the source reader retains allowlisted `name` and `objectId` only.
3. A single PostgreSQL transaction:
   - checks and locks the active worker lease;
   - inserts the immutable source page checkpoint plus its `PageSaved` audit event (only when new);
   - normalizes individual `(repositoryId, refName)` identities, their canonical `{"ObjectId":"..."}` payloads and SHA-256 fingerprints;
   - inserts the normalized ref observations in JSONB **batches of 250**, with a primary key on `(job_id, source_id)`;
   - checks all conflicts, and verifies the lease is still valid before committing.
4. On retry, cached pages are replayed using their request fingerprints. Their provisional observations are backfilled if they originated before this feature, without generating duplicate events or asset records.
5. Once the full immutable source snapshot is available, the existing F-013 normalizer commits the authoritative per-source catalog and completeness-aware missing detection as before.

**A source page never directly mutates the canonical `staging_assets` table**: an incomplete or failed discovery is not a valid basis for deleting or changing the source's current catalog. Provisional observations are keyed by the unique discovery job and cannot be mistaken for approved or finalized changes.

## Storage

`discovery_ref_observations`: `job_id`, canonical composite `source_id`, `repository_id`, `fingerprint`, `payload_json` (`jsonb`), and the source `resource_key` + `page_index`. A foreign key references the committed source page checkpoint, guaranteeing page provenance. A composite primary key prevents duplicate observations; an index on `(job_id, repository_id)` supports per-repository processing.

Provisional payloads and the final F-013 catalog both call the **same canonical ref normalizer**, avoiding disagreements in source identity or fingerprint. If unstable provider pagination returns the same ref name with a different SHA across pages, the page transaction rolls back and surfaces a `RefObservationConflict`, rather than accepting silently divergent versions.

## API and UI

`GET /api/jobs/{jobId}/ref-progress` is **admin/superadmin-only** and requires completion of first-login password change. Returns `jobId`, `checkpointedRefPages` and `observedRefs`, all derived from indexed PostgreSQL tables. Missing jobs return 404; staging-only jobs return 400. No raw refs, tokens or continuation markers are disclosed.

The Discovery workbench polls this count alongside the durable job status. The UI explicitly labels observations **provisional until discovery completes**, keeping them distinct from the canonical staging summary.

## Verification

- **Real PostgreSQL 17:** 1,000 + 250 references across pages, bounded ingests, correct canonical hashes, checkpoint replay, idempotency, progress counts, no premature catalog writes.
- **Crash/rollback:** same ref name with different OID on the second page rolls back both SQL batches and its page checkpoint/audit event; the first page remains unchanged.
- **Worker fencing:** an expired worker cannot persist a new ref page or observations; the new lease owner can resume safely.
- **Data validation:** duplicate keys within one page are rejected atomically; invalid/empty provider entries are filtered consistently with the source reader.
- **API/UI:** end-to-end provider scan yields provisional records whose fingerprints equal the finalized F-013 records; role checks for progress; Playwright displays progress while discovery is in flight.

## Immutable chunk manifests (next slice)

After the source snapshot is saved, the worker now prepares an immutable chunk manifest from these provisional observations and verifies the snapshot's full ref multiset in PostgreSQL. The manifest is exposed only when the discovery job succeeds; chunk range reads use the PostgreSQL source-ID index and recheck SHA-256. See [immutable ref chunk manifests](immutable-ref-manifests.md).

## Current limitations / next milestones

This first slice **persists Git ref observations incrementally** but still retains the final `DiscoveryInventory.Refs` collection and immutable full JSONB discovery snapshot in memory. Therefore **it is not yet full end-to-end constant-memory streaming**. No throughput or million-asset SLA has been benchmarked.

Next: build snapshot manifests that reference streamed chunks, stream the remaining source asset families, reconcile provisional assets into the canonical catalog after a successful scan without loading a complete inventory, introduce backpressure, retention/compaction, production schema migrations and load tests. Provider-side delta APIs and periodic scheduling remain separate work. None of this writes to Azure DevOps or GitHub.
