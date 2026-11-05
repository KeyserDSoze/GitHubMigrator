# F-024 — Externalize Graph groups into PostgreSQL source observations

F-024 completes the currently supported Graph evidence families (users,
groups, direct memberships) alongside raw Azure DevOps Git ACLs: for a
**leased, checkpoint-backed** source discovery, the immutable source
snapshot no longer contains the group records, even though they were
temporarily needed in the worker to traverse direct memberships.

## Source and snapshot compatibility

- The source reader still holds at most 1,000 Graph groups **temporarily**
  while issuing GET-only depth=1 membership calls. It returns `groups: []`,
  `groupsExternalized: true`, and the **distinct stored** `groupCount`.
- The source uses existing F-019 `discovery_group_observations` with
  `(job_id,source_id)` primary key, canonical `staging-v1` fingerprint,
  checkpoint page FK, and 250-record ingest. No new DB schema is required.
- Synchronous adapters, fake checkpoint stores lacking this capability,
  and pre-F-024 snapshots keep inline group behavior. Explicit markers
  distinguish an externalized empty array from a true zero-group inventory.
- The durable discovery service only accepts this marker with a stable,
  lease-owned source job and a count from 0–1,000.

## End-to-end integrity

A running worker first verifies its current PostgreSQL lease and
job/connection/snapshot binding, backfills old checkpoints under that
lease, then checks **both directions**:

1. Sanitized Graph group checkpoint `value` rows versus normalized group
   observations, including each group's display name and legacy descriptor.
2. For older inline snapshots, the inline group set versus observations;
   for externalized snapshots, `groups == []` and `groupCount` equal the
   stored indexed observation count.

Complete group coverage requires a contiguous page chain starting at zero
and a terminal page (including an empty group inventory). Same-count name
changes, incomplete pages, invalid JSON shape and count mismatches fail
closed. No group list needs to be sent back to the worker after discovery.

The F-020 direct Graph membership verifier now resolves the set of
authorized **source group descriptor observations** in PostgreSQL when
`groupsExternalized=true`. A membership page for an unlisted group still
fails, as do missing terminal pages when direct-membership coverage is
declared complete. These observations **do not** imply effective rights.

## Canonical staging and identity review

The leased F-013 catalog import keyset-reads group observations by
`(job_id, source_id)`, 250 at a time, renormalizes every canonical
`group` asset and fingerprint, and checks the exact `groupCount` before
committing. All imports share one source-scoped catalog transaction:
corrupt records or mismatched counts roll back the whole staged snapshot.
Manual staging rejects externalized group snapshots without the sealed
ref manifest and worker lease.

The F-004 identity mapping SQL now resolves legacy principals from
externalized Graph groups and raw ACL observations, not just legacy
inline `groups`/`aclEvidence`. This closes a source-review gap for
both F-023 ACL externalization and F-024 group externalization.

The discovery API summary and React workbench report the true count and
explain why group names are not present inline. Existing completed-job
`GET /api/jobs/{id}/groups` remains RBAC-protected and paginated.

## Tests

Real PostgreSQL integration includes 521 groups across checkpoint pages,
a direct membership referencing an externalized group, canonical staging
from three 250-row batches, fingerprint parity, empty array marker,
manual sync rejection, same-count payload tampering, count mismatch,
missing terminal page and correct unresolved ACL principal counting with
externalized groups **and** ACLs. The HTTP discovery integration verifies
persisted source snapshot and summary markers. All previous legacy,
integration, React/Playwright, Terraform tests remain in the CI matrix.

## What F-024 does not promise

A maximum of 1,000 group descriptors is still held while fetching their
memberships; project/repository metadata also remains bounded and inline.
No recursive Graph group expansion, effective authorization algorithm,
destination GitHub grants, production load benchmark, continuous polling,
approved target writes, or production schema migration rollout is
implemented. Durable checkpoint replay and canonical staging are
**building blocks** for a later incremental synchronization engine.
