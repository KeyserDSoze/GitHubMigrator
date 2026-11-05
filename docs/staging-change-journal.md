# F-025 — Immutable staging change journal (incremental sync foundation)

The F-013 canonical PostgreSQL staging catalog previously held only each
asset's **latest** state. A later discovery could overwrite a fingerprint or
mark a ref missing, but it was impossible to retrieve a previous scan's
precise change set. F-025 adds a durable, **read-only** ledger suitable for
future reviewed, incremental migration planning. It does **not** publish,
delete or grant anything on GitHub.

## Schema and upgrade

`staging_asset_changes` records `snapshot_id`, `source_connection_id`,
`asset_type`, canonical `source_id`, `change_kind`, nullable previous/current
SHA-256 fingerprints and resulting canonical revision. One event exists per
changed asset per snapshot, primary key `(snapshot_id,asset_type,source_id)`.
The source/snapshot/kind index supports aggregate summaries; the PK supports
ascending source ID pagination. No PATs, provider response bytes, ACL contents,
Git pack objects or arbitrary historical asset payloads are stored.

`staging_snapshot_syncs.journal_version` defaults to zero for all pre-F-025
snapshots; **version 1** is written atomically with a completed F-025 sync.
The phase-0 bootstrap uses additive `CREATE TABLE IF NOT EXISTS` and
`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`. This is **not** a production-
reviewed database migration. Existing snapshots cannot be reconstructed from
current staging rows; requesting their journal returns HTTP 409 rather than
pretending their delta was empty.

## Transaction semantics

Both synchronous legacy scans and F-024 leased, checkpoint-provenanced
discovery jobs use the **same journal code path**:

1. Acquire the existing per-source PostgreSQL transaction advisory lock.
2. Reject a previously committed snapshot from a different connection, an
   out-of-order source collection timestamp or an invalid manifest.
3. For each bounded 250-row staging UPSERT, compare incoming asset keys with
   the preceding canonical row **inside the same SQL statement/transaction**:
   - `Added`: no previous canonical row; previous fingerprint is null.
   - `Changed`: previous row is present and active with a different SHA.
   - `Restored`: previous row was marked missing, even if the fingerprint
     matches the original SHA. This is counted under F-013 `Changed` in
     the existing `StagingSyncResult`.
   - `Unchanged`: identical SHA and active row: **no journal event**.
4. **Only** for asset types with proven complete source coverage, record
   `Missing` events using prior fingerprints, then mark unseen active rows
   missing and increment their revisions. Partial Graph, ACL or ref coverage
   can never assert missing/deleted resources.
5. Persist the sync summary with `journal_version=1` and perform any
   existing worker lease-fencing check **before** the transaction commits.
   An invalid batch, lease failure or later catalog error rolls back both
   the new asset records **and all journal entries**.
6. Replay of a successfully committed snapshot returns the immutable existing
   summary and emits **no extra** journal entries.

The invariant for a successful version-1 journal is:
`Added = sync.Added`,
`Changed + Restored = sync.Changed`,
`Missing = sync.MarkedMissing`.
Summary reads check this directly, refusing an inconsistent journal.

## Protected endpoints

All routes inherit the existing password-change and admin/superadmin
authorization requirements from `/api/staging`.

| Route | Result |
| --- | --- |
| `GET /api/staging/snapshots/{snapshotId}/change-summary` | Snapshot/source + Added, Changed, Restored, Missing and Total counts. |
| `GET /api/staging/snapshots/{snapshotId}/changes/{assetType}?take=50&after=...` | Keyset page, max 100, using snapshot, type and ascending canonical source ID; reports change kind, old/new SHA and revision, but **never** payload. |

These routes return 404 for unknown syncs, 409 for pre-F-025 syncs,
400 for invalid asset types/pages, and 409 for summary integrity failure.
Supported types match the canonical catalog: project, repository, ref,
identity, group, membership, acl. They describe *observed source evidence*;
no raw ACE is an effective GitHub permission or an approved grant.

The React discovery workbench shows a compact IT/EN change summary after a
version-1 staging commit; old snapshots do not display a made-up zero delta.

## PostgreSQL integration coverage

- 1,202 additions across multiple 250-row UPSERT batches, immutable
  replay and deterministic multi-page keyset reads.
- Source rescan changes one ref but cannot mark an absent ref missing while
  coverage is incomplete; a complete scan records Missing.
- A later scan restores the exact old ref fingerprint, recording Restored
  (revision increments) while the older Missing journal remains queryable.
- A new fully unchanged scan produces no events.
- Invalid data in a second batch rolls back the previous batch's journal,
  catalog UPSERT and snapshot sync, then supports clean retry.
- Pre-F-025 sync returns explicit journal-unavailable; aggregate tampering
  fails closed; separate source connections do not leak their journals.
- JWT-protected API route, raw fingerprint-only page, paging errors and
  HTTP discovery-worker success are covered.

## Remaining work

This does not yet implement a periodic source scanner, change-data capture,
scheduled retries for external destinations, approved GitHub write execution,
transactional outbox, retention/compaction, per-source quota enforcement,
production schema migrations or scale benchmarking. Those require separate
feature gates, production-safe permission review and load validation.
