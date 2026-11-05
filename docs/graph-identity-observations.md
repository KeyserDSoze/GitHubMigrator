# F-015 — Durable Graph identity observations (first safe slice)

**Status:** per-page Graph identity observation ingestion implemented. This does **not yet externalize the identity list** from the immutable discovery snapshot or from F-004 governance. That compatibility boundary is deliberate.

## Current source-to-database flow

1. A leased F-008 Azure DevOps `DiscoverSource` job calls the Microsoft Graph user endpoint using its existing bounded, credential-isolated GET-only reader.
2. Each page is sanitized to the existing allowlist `descriptor`, `displayName`, `mailAddress`, `legacyDescriptor`. Unexpected provider JSON fields are excluded before checkpoint persistence.
3. The same PostgreSQL transaction holds the active worker lease, persists the immutable source page checkpoint, emits `PageSaved` only for a new page, and writes up to 250 **canonical identity observations** per batched SQL operation.
4. Records use the natural source descriptor as `source_id`, exactly the F-013 `identity` asset payload (DisplayName, Email, LegacyDescriptor), and its shared SHA-256 fingerprint. `(job_id, source_id)` is unique. Identical replay is idempotent; a conflicting descriptor, missing fields or a duplicate descriptor within one page rejects the whole page.
5. Source pages are immutable and lease-fenced. Cached Graph pages written before F-015 can be replayed under a live worker lease to backfill their identity observations.
6. When an immutable discovery snapshot has been saved but **before** the job may write canonical staging or report success, PostgreSQL compares the exact set of descriptors/display names/email/legacy descriptors in the snapshot and the provisional observation table. It uses a symmetric `EXCEPT` comparison; mismatches fail closed with `IdentitySnapshotMismatch`.
7. **Source identity coverage and permission proof remain separate:** failed Graph scopes still produce explicit partial coverage; observed users do not imply tenant-wide completeness, effective Azure DevOps permissions, target GitHub membership or authorization to grant access.

## Storage and APIs

`discovery_identity_observations` has `job_id`, `source_id`, `fingerprint`, `payload_json` (jsonb), `resource_key`, `page_index`. Its FK to `discovery_page_checkpoints` proves page provenance, while the PK enables indexed keyset pagination. Existing bootstrap DDL is additive; reviewed schema migrations are still required before production.

All routes inherit the platform's **admin/superadmin + completed-first-login-password** authorization.

| Method | Route | Semantics |
| --- | --- | --- |
| GET | `/api/jobs/{jobId}/identity-progress` | For running/completed `DiscoverSource` jobs: count persisted Graph pages and unique descriptors; *provisional* while running. Does not disclose identity data. |
| GET | `/api/jobs/{jobId}/identities?take=50&after=...` | Up to 100 identities in source-ID order, fingerprint and allowlisted metadata. Only for **Succeeded** source discovery jobs. 409 while queued/running/failed; 400 for invalid page size. |

The Discovery UI polls the progress data beside the Git ref counters. It calls out that a finished job establishes snapshot/observation consistency, **not verified effective permissions**.

## Verification

- xUnit: canonical source identity ID/payload/fingerprint equality with F-013; changes in display name change the hash, not the identity key.
- PostgreSQL 17: ingest 1,001 users across two pages, 250-record SQL batches, identical replay without duplicate events or records, exact hash compatibility, bounded keyset reads.
- PostgreSQL: second-page descriptor collision **rolls back both JSONB batches and checkpoint/audit**, retains all earlier pages; duplicates within a single page fail atomically.
- PostgreSQL: expired worker cannot insert new identities; new owner can continue from stored evidence.
- PostgreSQL: an immutable snapshot is reconciled with stored observations, with automatic backfill after rollout; a modified snapshot identity is detected and rejects the job.
- Real ASP.NET/JWT/PostgreSQL integration: live mocked-Azure GET-only source discovery persists observations, checks the post-success paginated route's RBAC, and verifies fingerprint parity against finalized staging.
- Playwright: the queued worker's provisional identity progress is visible beside existing Git refs without suggesting approved grants.

## Subsequent F-018 implementation

Leased Azure DevOps workers now externalize their full Graph user lists into PostgreSQL, replacing the inline snapshot list with `identitiesExternalized: true` and an exact `identityCount`. F-004 and F-005 use the F-016/F-017 paged database readers, and F-013 stages identity observations in bounded 250-row batches with fail-closed page/observation verification. See [externalized Graph identities](externalized-graph-identities.md).

## Limits and next increments

Graph users still accumulate into the legacy `DiscoverySnapshot.Identities` dictionary and JSONB snapshot so F-004 identity mapping, F-005 previews, and the existing completeness checks retain their original behavior. **This is not constant-memory identity discovery.**

Before externalizing them, F-004/F-005 must consume **paged/snapshot-bound identity data** instead of assuming the inline list is complete; manifest sealing must prove identity presence and metadata hashes, and governance must continue fail-closed on missing scope and unresolved principals. Further work includes streaming groups/memberships/ACLs, provider-side deltas, job scheduling/backpressure, retention/compaction, production DDL migrations, and load testing. No GitHub writes or automatic approvals are enabled.
