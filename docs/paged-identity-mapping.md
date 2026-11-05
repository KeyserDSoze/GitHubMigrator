# F-016 — Bounded F-004 identity mapping reviews against PostgreSQL

**Scope:** move F-004 identity draft review, source enumeration, identity validation and unresolved ACL principal counts to bounded PostgreSQL reads. This is an incremental consumer migration for future source identity externalization, **not** identity export, permission assignment or a migration approval.

## Immutable source selection

- A new `IIdentitySnapshotReader` / `PostgresIdentitySnapshotReader` returns the existing discovery snapshot **with the `snapshot.identities` and `refs` arrays removed by PostgreSQL before serialization to .NET**. Projects, groups, raw ACL evidence, coverage and governance flags remain available.
- For successful F-015 source jobs containing normalized Graph identity observations, F-004 uses the indexed `discovery_identity_observations(job_id,source_id)` table, strictly scoped to that immutable snapshot/job ID.
- Before using observations, PostgreSQL checks the source job is `Succeeded`, counts match, and a **bidirectional multiset difference** of descriptor, display name, email and legacy descriptor is empty. If any mismatch is found the request fails closed with `IdentitySourceMismatch`. In-progress observations are not treated as complete.
- Old snapshots without Graph observation rows continue to use their **immutable inline JSONB identity array**, with `jsonb_to_recordset` queries. This is a DB-side bounded page read, not a full in-process identity list.
- No query reads a source identity from the global latest staging catalog: doing so would conflate a draft's immutable snapshot with later snapshots of the same source connection.

## F-004 behavior

- `GET /api/identity-mappings/{snapshotId}/sources?destinationConnectionId=...&take=100&after=...` returns at most 100 users with a source-ID keyset cursor. Existing admin/superadmin, first-login password-change and destination-connection validation apply.
- Existing `GET`/ `PUT /api/identity-mappings/{snapshotId}` now build previews from PostgreSQL counts and the first 100 unmapped source IDs, rather than loading all users. `PUT` validates only the submitted source descriptors using a bounded SQL `ANY(...)` membership check; it retains revision compare-and-swap and the existing 10,000 mapping limit.
- Distinct ACL descriptors unresolved against source user and Graph-group legacy descriptors are counted in PostgreSQL. Their count remains a review warning, **not** a derived effective permission.
- The web mapping editor fetches one page of 100 identities at a time, shows accurate **global** discovered/mapped/unmapped counts and page-local search, and preserves all draft edits when navigating to another page. Saving a draft always submits the *complete* current mapping set, never just the visible page.

## Security boundaries

- No changes to target GitHub organization membership, PAT scopes, invitations or source ACLs.
- `ReadyForGrants`, `TargetAccountsVerified`, and `EffectivePermissionsVerified` remain **false** without independent evidence. A stored GitHub login is only a draft.
- Source observation mismatch blocks F-004; incomplete source identity coverage still emits `SourceIdentitiesIncomplete` rather than implying no users.
- Requests bound to a different destination connection or to unknown source IDs are rejected. Draft revision CAS prevents stale administrators from replacing another's updates.
- The API rejects more than 100 source rows per page, bad cursors, and more than 10,000 submitted mappings.

## Tests

- Real PostgreSQL: 1,205 successful-job Graph observations, keyset paging across first/middle/final pages, F-004 preview counts, two draft mappings on different pages, ACL unresolved counts, unknown identity rejection and preserved CAS revision.
- Real PostgreSQL: 305 legacy inline identities with **no** F-015 rows are queried in bounded JSONB pages and validated by source ID.
- Real PostgreSQL: a completed job with an altered observation descriptor or payload fails the symmetric snapshot comparison even when row counts match.
- ASP.NET/JWT/EF/PostgreSQL: paging endpoint admin-role checks and cross-page identity draft saving through real HTTP `PUT`.
- Playwright: 101 users across two pages; saving a draft retains edits from the first page after advancing, and the mapping is restored when returning.
- Existing .NET/xUnit, PostgreSQL, Playwright and Terraform CI remain mandatory.

## Limitations and next step

The old immutable snapshot still stores the full `DiscoverySnapshot.Identities` JSONB array, and Azure DevOps Graph discovery still gathers users in process memory. **F-016 reduces F-004 review/read memory, not source-discovery memory.**

F-005 plan previews and legacy Discovery user listing also still consume inline source identities. Do not externalize source identities until those consumers, coverage semantics, safe backfill and successful-job manifest consistency have been updated. Additional work: handle >10k mappings as sparse paged drafts, searched server-side queries, streamed group/membership/ACL evidence, provider-side delta scans, load benchmarks and reviewed production schema migrations.
