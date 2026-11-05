# F-014 — Git ref source collection without full in-memory ref arrays

**Scope:** the durable F-008 Azure DevOps `DiscoverSource` worker now externalizes Git reference metadata into PostgreSQL during page retrieval. This removes the large Git ref list from the **provider-side source reader** as well as the downstream staging service introduced by PR #12.

## Durable source workflow

1. Only a lease-owned background source discovery uses this mode. The source reader issues bounded, fixed-host Azure DevOps read-only GETs for each repository's Git refs.
2. Each 1,000-entry page is allowlisted to ref `name` and `objectId`, inserted as an immutable F-008 checkpoint, and atomically normalized into F-014 `discovery_ref_observations` in 250-asset batches. Retry of a committed page uses PostgreSQL without repeating its provider GET.
3. Instead of accumulating a full `List<RepositoryRef>` in the worker, the reader increments a validated ref count. The returned inventory contains `refs: []`, `refsExternalized: true` and `refCount: N`.
4. The job owns a stable immutable snapshot ID. A saved externalized inventory retains the true count and the marker on crash recovery. The legacy `Refs` array is deliberately empty: it **does not indicate an empty source repository**.
5. The F-014 manifest builder checks that the immutable snapshot explicitly externalizes refs, carries zero inline ref entries, and has an expected count matching the PostgreSQL ref observations. It then constructs and seals bounded ordered chunks with SHA-256 digests. For legacy inline snapshots, strict `(repositoryId, name, objectId)` comparison remains unchanged.
6. The F-013 staging path reads only a PostgreSQL projection of the source snapshot (without ref payloads), rechecks manifest and chunk hashes, and UPSERTs verified chunks into canonical staging in one source-scoped transaction. No false missing/deletion is inferred from a truncated source or a failed job.
7. Manifest data becomes accessible to admins only **after** the source worker commits canonical staging and marks its durable job `Succeeded`. Failed or queued jobs cannot publish an apparent complete inventory.

## Compatibility and safety

- **Existing synchronous discovery** retains its inline Git refs (the original F-003 behavior). New optional `refsExternalized` / `refCount` fields are absent or false in old persisted snapshots; clients use the previous `Refs.Count` semantics.
- The Discovery summary API uses `refCount` only for externalized snapshots; otherwise it counts inline refs. The web workbench labels the metadata as stored in PostgreSQL rather than incorrectly showing zero refs per repository. For a per-repo inventory of externalized refs use the published ref manifest and canonical staging pages.
- The **legacy manual staging endpoint and `StageSnapshot` job** reject externalized snapshots with `ExternalizedSnapshotRequiresManifest`. Only the leased `DiscoverSource` path can apply them via the verified manifest. This prevents an empty inline list from becoming a false authoritative staging sync.
- Connection tokens, unknown source fields, provider continuation markers and raw permission details are never placed in the new Git ref observations. The data is read-only to Azure DevOps; there are no GitHub writes, approvals or permission-grant operations.

## Verification

- xUnit reader: a 1,001-ref source split across two Git GET pages produces `refs: []` with `refsExternalized: true` and the correct `refCount`. A transient failure on page two resumes from its committed checkpoint without downloading page one or other completed metadata again.
- PostgreSQL: persist a 1,250-ref externalized source snapshot, assert no inline refs in the immutable JSONB inventory, build five 250-ref chunks and reconcile them into the canonical staging catalog. A missing observation refuses manifest publication; the legacy sync refuses the metadata-only snapshot.
- End-to-end real API + PostgreSQL: queued Azure DevOps discovery records accurate counts in both JSON snapshot detail and summary history while the final staging and manifest agree. Provider requests remain GET only.
- Playwright: the Discovery UI shows the total 1,250 refs and explains that repository-level refs are stored in PostgreSQL, without claiming “0 refs”.

## Remaining memory, scale and operations work

This is **bounded-memory Git ref collection at the application-reader boundary**, not overall constant-memory discovery. Projects/repositories, Graph identities/groups, ACLs and TFVC evidence are still materialized in process memory. The legacy full JSONB source snapshot still contains those other asset families; the API listing retains its existing 25-snapshot bound. The per-page source payload remains bounded by a 1 MiB checkpoint limit, 30 pages per repository and 30,000 refs per repository. No blanket million-asset throughput or performance SLA has been measured.

Next steps: page-chunk project/identity/ACL evidence, source delta scans with reliable continuation checkpoints, periodic scheduling and backpressure, retention/compaction, benchmark fixtures for 10k–1m assets, production schema migrations, and reviewed approval gates before any GitHub target writes.
