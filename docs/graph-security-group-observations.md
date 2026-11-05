# F-019 — Durable Graph security-group observations

**Scope:** first bounded-source-page observation slice for Azure DevOps Graph **security groups**. Groups remain in the immutable discovery snapshot and are consumed by existing F-004/F-005 and security readers. This increment does **not** externalize all groups, traverse transitive membership, derive effective permissions, or grant GitHub access.

## Collection and checkpoint integrity

- Only the F-008 leased, GET-only Azure DevOps Graph security discovery can store group observations. The Azure DevOps security reader allowlists **descriptor, displayName, legacyDescriptor** before persisting its existing `security-groups` page checkpoint. Unknown provider fields, credentials and raw HTTP headers are excluded.
- New `discovery_group_observations` PostgreSQL records contain the immutable discovery-job ID, descriptor source ID, canonical F-013 group fingerprint/payload JSONB, `resource_key`, and page index. The `(job_id, source_id)` PK and FK to the exact checkpoint page establish provenance and support keyset reads.
- The same lease-fenced PostgreSQL transaction saves the page, normalizes observations in **up to 250-row SQL batches**, and emits a single `PageSaved` audit event. A duplicate descriptor within a page fails. Repeated identical descriptors on later pages are idempotent; conflicting metadata fails the **whole** new page, including earlier batches.
- On retry, an existing cached checkpoint can backfill observations without another Azure DevOps GET or duplicate audit events.
- Before updating F-013 staging, the durable worker checks its live lease and reconciles the saved immutable `groups` list against the full observation multiset in **both directions** using SQL `EXCEPT ALL`. An equal-count replacement is rejected as `GroupSnapshotMismatch`. If the source claims complete group coverage, the corresponding group checkpoint must exist.
- Legacy synchronous source scans and group consumer paths remain unchanged. F-013 staging and draft/governance safety rules are unchanged. Effective permissions remain explicitly unverified.

## Protected APIs

The existing `PasswordChanged` and `admin`/`superadmin` policies apply.

| Method | Route | Result |
| --- | --- | --- |
| GET | `/api/jobs/{jobId}/group-progress` | Counts saved Graph group pages and unique descriptors for a `DiscoverSource` job. Provisional while in progress; no sensitive group details. |
| GET | `/api/jobs/{jobId}/groups?take=50&after=...` | Keyset-paged (maximum 100) allowlisted group metadata, source descriptor and fingerprint **only after job success**. Returns 409 otherwise and 400 for invalid pagination. |

The Discovery workbench shows progress in IT/EN and always distinguishes raw Graph evidence from effective permission validation.

## Verification

- PostgreSQL integration: 521 groups across 2 pages, canonical fingerprint parity and 250-row batching, keyset queries, idempotent source replay and no duplicate audit events.
- Cross-page contradictory metadata after 250 records fails and rolls back the entire page/observation insert.
- Expired lease rejects new observations and checkpoints.
- An immutable snapshot with altered metadata but the same number of groups is rejected before staging.
- Real HTTP/JWT/PostgreSQL provider stub: group page progress, successful-job-only group listing, invalid `take` and unauthorized requests.
- Playwright: the worker displays provisional Graph group counters in the Discovery workbench.

## Explicit limitations

This is **not yet full constant-memory security discovery**. The Azure DevOps reader still builds the group list to enumerate memberships and the ACL reader still accumulates raw entries. Snapshot `groups`, `memberships`, and `aclEvidence` arrays remain inline for backwards compatibility. Upcoming steps: indexed membership-edge observations, durable group traversal, raw ACL evidence with source-scoped provenance, snapshot-bound paged security consumers, bounded-source externalization, test load fixtures, reviewed production schema migrations, retention and recovery policies.

No GitHub destination write worker, approvals, permission assignment, inherited-rights inference or migration completion claims are introduced.
