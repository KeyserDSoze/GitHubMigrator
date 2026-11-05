# F-017 — Immutable PostgreSQL evidence for F-005 migration plans

**Scope:** move F-005 Migration Plan's *reference counts* and *identity source checks* to PostgreSQL, bound to the specific immutable discovery snapshot chosen by the operator. This is a read-only plan increment. No target repositories, permissions or migration operations are created.

## Why this matters

F-014 leased Azure DevOps source discovery already stores Git refs by page in PostgreSQL and marks its original snapshot `refsExternalized: true, refs: [], refCount: N`. Prior F-005 preview code used only `inventory.Refs.Count(...)`; it therefore displayed **false zero ref counts** for new durable discoveries. F-016 made F-004 reviews paged but the F-005 plan still loaded the whole identity/ref arrays.

## Implementation

1. F-005 uses F-016's **slim snapshot metadata projection**, so the complete `refs` and `snapshot.identities` arrays are stripped by PostgreSQL before reaching the preview process. Repository/project metadata and raw ACL/coverage flags remain available.
2. A new `IPlanRefEvidenceReader` / `PostgresPlanRefEvidenceReader` produces one snapshot-bound per-repository ref-count dictionary. The caller requests it only when at least one repository is selected for inclusion.
3. For **legacy inline snapshots** it groups source `refs` directly from immutable JSONB with `jsonb_to_recordset`, without materializing individual refs in the API. Counts must agree with the inline array length.
4. For **externalized snapshots** it requires a matching completed `DiscoverSource` job, correct source connection, matching sealed F-014 manifest, contiguous chunk indexes, exact totals and root descriptor SHA-256.
5. It then compares the complete `(repositoryId, refName, objectId)` **multiset in both directions** between immutable F-008 source pages and normalized F-014 observations using PostgreSQL `EXCEPT ALL`. Even if a row is replaced after success and the aggregate count remains constant, plan review fails closed.
6. Only after these checks does the reader use indexed PostgreSQL grouped counts. No ref page bodies are returned to F-005.
7. If a saved F-004 mapping draft contains source descriptors not present in the same snapshot's verified identity catalog, the plan refuses it as `IdentityMappingSourceMismatch` instead of displaying GitHub-login candidates derived from inconsistent evidence.
8. Existing plan validation, history-strategy warnings, idempotent revisioned drafts, target-name/preflight checks, unverified effective permissions and live GitHub membership/availability blockers remain unchanged. **`ReadyForApproval` stays false.**

## APIs and UI

No new public endpoint is needed: the existing admin-only `POST /api/migration-plans/preview` now returns accurate `repositories[].refCount` for both snapshot formats. `GET/PUT /api/migration-plans/drafts/{snapshotId}` uses the same read-only evidence validation when previewing selections. The React plan already shows each returned count; it will now display the PostgreSQL-backed number rather than a false zero.

- Missing, unfinished or inconsistent externalized source evidence: `400 PlanRefEvidenceUnavailable` / `PlanRefEvidenceMismatch`, never a zero count and never an approval.
- Unsupported source descriptors in a persisted identity draft: `400 IdentityMappingSourceMismatch`.
- Existing role/first-login-password-change authorization continues to apply.

## Verification

- PostgreSQL 17: two completed snapshots of the same source have different Git ref counts, and F-005 retains each **historical snapshot's own** counts (never the latest mutable staging state).
- PostgreSQL: an externalized snapshot cannot be presented as complete before source-job success.
- PostgreSQL: a persisted object-ID mutation with unchanged row count fails the page/observation multiset verification.
- PostgreSQL: old inline snapshots aggregate accurate per-repository counts from JSONB.
- Full HTTP/JWT/PostgreSQL + mocked GET-only Azure DevOps: a successful async discovery persists `refs: []` with one observed ref; F-005 reports `refCount: 1`, leaves approval blocked and makes no new provider calls.
- Unit: no externalized Git ref count is silently interpreted as zero when the evidence reader is absent.

## Performance and limits

This keeps **all Git ref and identity list payloads out of the F-005 review process**, but the plan still materializes source project/repository metadata and raw ACL data. On every externalized preview with selected repos the current SQL guard checks the full saved ref-page/observation multiset; this is fail-closed but can be expensive at enterprise scale. Add persisted verification checkpoints, indexed content summaries and reproducible explain/analyze benchmarks before promising low latency for very large scans.

F-005 uses the existing raw ACL evidence only for advisory candidates under verified upstream permission flags; it never treats Graph users as GitHub memberships. F-004/F-005 and historical snapshots are now prepared for the next source-side identity externalization slice. Graph groups, memberships, ACLs and other assets still need chunk-driven collection and reader migration. Production schema migration/retention, periodic deltas, governed GitHub execution and reconciliation remain future work.
