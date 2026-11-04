# F-008 — Durable Azure DevOps page checkpoints (first granular slice)

**Implemented scope:** the read-only Azure DevOps Services discovery worker persists immutable checkpoints for **project lists, project capabilities, repository enumeration, Git refs, Graph users, Graph groups, direct group memberships and raw Git ACL evidence**. On a retry of the same discovery job, saved pages are reconstructed from PostgreSQL, their request fingerprints are checked, and those provider pages are not downloaded again.

The design extends the stable whole-snapshot checkpoint in [asynchronous discovery](async-discovery.md) and the F-008 PostgreSQL queue in [durable jobs](durable-jobs.md).

## Collection flow

1. An admin enqueues one `DiscoverSource` job for a verified source connection.
2. The worker claims a durable, lease-fenced attempt.
3. The source reader checks `discovery_page_checkpoints` for `(job_id, resource_key, page_index)` before each eligible GET.
4. An existing page is returned from `safe_payload_json`, **only if** the SHA-256 hash of the exact provider request URL matches the saved `request_hash`.
5. Otherwise the reader issues the fixed-host Azure DevOps GET, retains only known fields, stores an immutable PostgreSQL page under the current worker lease and records a `PageSaved` audit event.
6. The reader reconstructs the continuation marker from the saved page and proceeds to the next page, even after a process crash.
7. A successful full inventory becomes an immutable `migration_discoveries` snapshot (job ID = snapshot ID), then the F-013 staging hash catalog is updated.
8. If a crash occurs after the final snapshot commit, the original F-008 checkpoint resumes immediately from that snapshot without source requests.

## Supported pageable resources

| Resource key | Azure DevOps API | Safe persisted fields |
| --- | --- | --- |
| `projects` | `/_apis/projects` (100 entries per request) | `id`, `name` |
| `refs:{repoId}` | `/_apis/git/repositories/{repoId}/refs` (up to 1,000 refs/request) | `name`, `objectId` |
| `graph-users` | `/_apis/graph/users` | `descriptor`, `displayName`, `mailAddress`, `legacyDescriptor` |
| `capabilities:{projectId}` | `/_apis/projects/{id}?includeCapabilities=true` | `capabilities.versioncontrol.sourceControlType` |
| `repos:{projectId}` | `/{projectId}/_apis/git/repositories` | `id`, `name` |
| `security-groups` | `/_apis/graph/groups` | `descriptor`, `displayName`, `legacyDescriptor` |
| `security-members:{sha256}` | `/_apis/graph/Memberships/{groupId}` | `containerDescriptor`, `memberDescriptor` (direct edges only) |

Membership pages use the opaque `X-MS-ContinuationToken` and are persisted separately. A repeated cursor produces `MembershipsPaginationIncomplete` and prevents a false complete-coverage claim. These are **direct edges only**, not transitive membership or effective authorization.
| `security-acl:{sha256}` | `/_apis/accesscontrollists/{GitNamespace}` | `token`, `inheritPermissions`, `acesDictionary` containing *only* descriptor, allow/deny masks |

The source reader retains its limits of 30 pages per project/ref/user collection, 2,000 projects, 30,000 refs per repository and 15,000 users. The security reader adds at most **20 group pages and 20 direct-membership pages per group**, with global bounds of 1,000 groups and 20,000 membership edges. Repeated Graph continuation tokens or caps mark membership coverage **incomplete**, never verified.  Per-page checkpoint payloads are bounded to 1 MiB of UTF-8 metadata. Project, ref and user metadata is still collected in memory until the final source snapshot is saved. **This is page-level replay, not full streaming ingest.**

## Data integrity, authorization and recovery

- `discovery_page_checkpoints` has a composite PK on `(job_id, resource_key, page_index)`, a foreign key to `migration_jobs`, an opaque `next_continuation`, a SHA-256 request fingerprint and a `jsonb` allowlisted response payload.
- `INSERT ... SELECT FROM owner ... ON CONFLICT DO NOTHING` under a PostgreSQL `FOR UPDATE` lease check means an expired/reclaimed attempt cannot append new pages. Two attempts cannot silently change a checkpoint.
- The canonical payload is verified using semantic JSON equality after PostgreSQL's `jsonb` normalization. Request mismatches fail closed.
- A transient project, repository, Git ref, user, group, membership or ACL read error causes a leased durable scan to retry, rather than silently interpreting missing data as verified absence. Permanent scope/permission problems remain visible through the inventory's partial-coverage semantics.
- No PAT, arbitrary API JSON, provider error body or authorization header is persisted as a checkpoint. Opaque continuation tokens are stored only in PostgreSQL, never returned in the job status API.
- Data is partitioned by job ID. New scans have distinct job IDs and do **not** reuse older pages: this avoids importing stale page contents into new source snapshots.
- These source reads **never** grant access, create repositories or write to GitHub.

## Tests

- Reader unit: transient error on page two of 1,001 Git refs, retry without re-fetching page one or the projects page; correct opaque continuation and excluded unknown/secret-like fields.
- Reader unit: mismatched request fingerprint aborts before calling Azure DevOps.
- PostgreSQL integration: immutable checkpoints and append-only `PageSaved` audit, JSONB key-order replay, stale lease rejection, active lease acceptance, conflicting request fingerprint protection.
- Full API integration: a real GET-only Azure DevOps discovery job creates three safe page checkpoints, final snapshot and normalized staging result.
- Existing queue, HTTP RBAC, browser and Terraform test suites continue to run.

## Per-page normalized Git ref observations (F-014)

Source Git ref pages now also produce normalized **provisional** PostgreSQL observations, committed atomically with each page checkpoint. The new admin-only per-job progress endpoint shows the saved page/ref counts; the canonical F-013 catalog changes only after the complete snapshot has been accepted. See [F-014 implementation and limits](streamed-ref-observations.md). Collection still builds the full immutable inventory in memory at the end.

## Not yet implemented — scalability roadmap

This increment does **not** persist progress for TFVC trees, arbitrary Work Items/pipelines/Boards or provider-side delta scans. Project capabilities and repository lists are checkpointed as single-response sections, not pageable server-side listings; the Azure REST responses may still be large. In-flight provider GETs that fail *before* the page is committed may be repeated. The Azure DevOps REST API does not give a guaranteed point-in-time snapshot across separate page requests; changes during a long scan can require a new verification pass.

Next work: checkpoint TFVC and workload sources where appropriate, move large collections directly into chunked staging tables without retaining the whole inventory in RAM, add provider-side delta synchronization where supported, scheduling, rate limit backpressure, production EF migrations and measured load tests (10k–1m assets). Until these are delivered, no enterprise throughput claim is warranted.
