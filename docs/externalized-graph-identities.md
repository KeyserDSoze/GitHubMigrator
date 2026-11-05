# F-018 — Durable Graph users externalized from source discovery

**Scope:** complete the first source-to-canonical-staging Graph identity streaming path on top of F-015 observations, F-016 paged identity mapping and F-017 snapshot-bound plan reviews.

## Source acquisition

A leased Azure DevOps `DiscoverSource` worker fetches Graph users through allowlisted, GET-only Microsoft-hosted REST pages. Each page commits its sanitized metadata to `discovery_page_checkpoints` and canonical `discovery_identity_observations` in one lease-fenced PostgreSQL transaction (250-row SQL batches). Instead of building a dictionary/list of full Graph user records in application RAM, the reader queries the indexed **distinct observed-user count** after each page. A maximum of 15,000 source users and 30 checkpoint pages remains in force.

The stable immutable discovery inventory records:

- `snapshot.identities: []`;
- `identitiesExternalized: true`;
- `identityCount: N`, the number of unique normalized descriptors actually saved.

A zero-length inline identity list is **not** an assertion of zero source users. `identitiesComplete` retains its independent meaning; a Graph API permission/scope error cannot become silent, false inventory completeness. Synchronous and older source snapshots continue to store inline identities.

## Consistency and governance

Before staging, the job reads its immutable Graph checkpoints back under the live PostgreSQL lease and performs a bidirectional **multiset comparison** of `(descriptor, displayName, mailAddress, legacyDescriptor)` between sanitized source-page rows and normalized observations. Missing or changed evidence, invalid externalized count, unexpected inline identities, or a supposedly complete inventory without a Graph page is rejected as `IdentitySnapshotMismatch`.

F-004 and F-005 independently enforce successful source-job completion before consulting externalized identities. They use snapshot-bound PostgreSQL identity pages (100 per request), validate the same source page/observation metadata even after completion, and do not fall back to an empty inline array when `identityCount=0`. Historical inline snapshots retain their original JSONB-based path.

**Effective permissions and GitHub memberships remain unverified.** No identity record or draft login creates a GitHub grant, invitation, approval or migration.

## Canonical staging

For externalized identity snapshots, manual legacy `StageSnapshot` and HTTP staging refuse the operation with `ExternalizedSnapshotRequiresManifest`. The durable `DiscoverSource` worker must own the source job's active lease, verify the checkpointed Graph evidence, and prepare the Git ref manifest.

The F-013 staging transaction then imports source projects/repositories/other nonexternal asset families, reads the normalized Graph observations **250 at a time by indexed source-ID cursor**, recomputes each row's canonical `identity` SHA-256 and applies the existing revisioned UPSERT. Git refs continue through their own immutable manifest chunks. The entire source-scoped catalog transaction is rolled back if a row hash, count or lease check fails. Missing detection remains conditioned on `identitiesComplete`: a partial Graph scan cannot mark unseen identities as deleted.

The observed/added/changed/unchanged counters include the persisted Graph records, not the empty inline identity array.

## API and UI compatibility

Discovery summaries and persisted details expose the true `identityCount` and `identitiesExternalized` marker. The React discovery page displays the count and explains where metadata is available; it never presents the empty inline list as proof that a tenant contains no users. Identity Mapping remains the bounded paged reader.

## Verification

- xUnit Azure DevOps: 1,001 users across two Graph API pages, failure on the second page, replay of the first without another provider GET, correct externalized count and zero inline user records.
- Real PostgreSQL 17: 1,001 users in two source pages → immutable `identityCount` → active-lease evidence verification → Git ref manifest → canonical identity staging in bounded 250-row operations → completed F-004 paged reader. Verify count and fingerprint parity.
- PostgreSQL rollback: modify an observation fingerprint after the evidence is committed; a blocked stage rolls back all project and user writes, not just the last batch.
- Existing PostgreSQL idempotency, conflicting-descriptor, expired-lease, manifest and delta tests remain.
- HTTP integration with mock Azure DevOps: completed source job saves no inline identities, exposes the true count, and F-004 still finds the saved user without any provider write.
- Playwright: externally stored identities are labeled in Discovery rather than incorrectly showing “no identities”.

## Not implemented

This does **not** yet make all source discovery constant-memory. Project/repository definitions, group membership and ACL evidence are still accumulated in-process; the full discovery inventory continues to persist these families as JSONB. F-015 identity observations are indexed but no separate signed identity chunk manifest exists; their integrity is currently proven against the immutable source pages and canonical hashes.

Next: stream Graph groups/memberships and ACL evidence, immutable chunk manifests for all families, delta polling and scheduling, schema migrations, retention/backpressure and reproducible high-volume benchmarks. GitHub execution requires explicit plan approval, controlled writes and fail-closed reconciliation; none is enabled by this feature.
