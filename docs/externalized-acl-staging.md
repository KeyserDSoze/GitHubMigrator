# F-023 — Externalized raw Git ACL evidence and bounded canonical staging

This increment extends F-022 and completes the *raw Git security ACL data path* for leased
Azure DevOps source discovery. ACL/ACE evidence is not effective permissions or a grant;
GitHub target access is never changed by discovery or staging.

## Source and snapshot shape

Production leased discovery writes safe `security-acl:<sha256(token)[0..40]>`
pages, with exact-page provenance and canonical normalized ACE records in PostgreSQL.
It no longer gathers the repository-level `List<RepositoryAclEvidence>` (including
project ACL copies per repository) in the worker heap.

The immutable `DiscoveryInventory` for such a job stores:
- `aclEvidence: []`
- `aclsExternalized: true`
- `aclCount`: **distinct raw source ACEs**, keyed by security token and descriptor,
  not project ACEs multiplied by downstream repositories
- `securityCoverage.aclsCollected`: whether every required scope was read.

Older snapshots and the synchronous discovery path retain inline evidence and
their original semantics. An empty inline array alone cannot mean no source ACLs.

## Database verification and keyset staging

Before preparing a Git-ref manifest or writing canonical staging:
- Verify active, fenced lease and source snapshot/connection binding.
- Backfill old checkpoint observations without provider GET or duplicate audit events.
- Verify raw source checkpoint ACEs ↔ indexed normalized observations with
  symmetric multiset comparisons, using `EXCEPT ALL`.
- For legacy inline snapshots, also verify distinct source raw ACLs against
  observations. For externalized snapshots, **do not interpret the empty ACL
  array as expected zero**: require an exact `aclCount` match and reject inline data.
- Reject unexpected project/repository scope, forged page key, invalid continuation
  and missing empty pages when ACL coverage is complete.

The F-013 staging transaction then scans `discovery_acl_observations` using
`(job_id,source_id)` keyset pages of at most **250 raw ACEs**. It checks that
each ACE has a canonical source ID, payload and SHA-256 fingerprint, and that
its security token belongs to the enumerated Git repositories.

**Project ACL expansion is deliberate:** a raw `repoV2/<project>` ACE is
staged once for each Git repository in that project as `scope="project"`;
a `repoV2/<project>/<repo>` ACE is staged once as `scope="repository"`.
The output uses exactly the pre-existing F-013 4-field natural key,
payload and `staging-v1\nacl\n...` fingerprint, preserving change
detection and catalog browsing. Only Git repository mappings, not ACEs,
are kept in an in-memory lookup. SQL UPSERT flushes batches of at most 250
staged records, including when one project ACE expands to many repositories.

The **200,000 normalized asset cap** applies to the *expanded* results plus
refs/identities/memberships/projects. Overflow, malformed payloads, count
mismatch, or unknown scope rolls back the complete staging transaction.
Incomplete source ACL coverage cannot mark unseen ACL assets missing.
Staging without the leased sealed ref manifest is rejected.

## Admin UI / endpoints

The existing admin/superadmin ACL progress and completed-job paged ACL
endpoints remain unchanged. Discovery summaries and the workbench now show
`aclsExternalized` and `aclCount`; the Security tab states that raw ACEs
are in PostgreSQL rather than presenting an empty inline list as 'none'.

## Verification

PostgreSQL integration tests exercise 521 repository ACEs + one project ACE
across two Git repositories, normalized 250-row staging, duplicated project
ACE projection with identical payload but distinct natural keys, complete
empty pages, count mismatch, altered same-count ACE values, legacy
compatibility, snapshot metadata/API reporting, and staging rollback.

## Explicit limitations

This is a bounded-memory **ACL evidence** path. Git repository/project scope
metadata and Graph groups remain inline in the discovery snapshot; per-token
sanitized source pages are still bounded by the existing 1 MiB cap. No effective
permission engine, destination GitHub write, approval flow, continuous delta
scheduler, production performance benchmark, or schema migration rollout is
claimed in F-023.
