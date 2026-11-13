# F-029 — Immutable operation manifests and explicit denied-only authorization

F-029 extends F-025–F-028 with a versioned, immutable, **non-executable**
operational specification for each source-to-GitHub mapping. The workbench
shows what would need to happen before a migration can be executed. F-029
**does not** create repositories, push Git data, assign permissions,
approve target writes or start an execution worker.

## Manifest creation

`POST /api/incremental-preparations/{id}/operation-plans` takes
`{expectedPreflightSha256}`. The source-scoped PostgreSQL advisory lock
serializes creation against F-013 staging and F-026–F-028 review changes.
Before writing anything, the backend checks:

- F-026 `Prepared` state and independently approved, current F-027 review.
- F-028 proof SHA, current snapshot, verified source/destination and
  5-minute observation freshness.
- Complete symmetric `EXCEPT ALL` reconciliation of immutable F-025
  journal and F-026 items, including identity, change type, fingerprint,
  revision, risk category and NeedsReview state.
- Streamed recomputation of the F-026 SHA-256 digest, detecting
  equal-count tampering after approval.
- One to ten distinct Git source repositories with `GitMirror` history
  strategy (other strategies explicitly rejected). Ref-delta counts are
  aggregated in PostgreSQL without collecting the full ref catalog in RAM.

Each source repository generates six ordered, dependent operation nodes:
`ReviewTargetName`, `ReviewSecurityMapping`, `TransferGitHistory`,
`ReconcileGitRefs`, `VerifyTransferredRefs`, `FinalizeMigrationAudit`.
**All** nodes have `state=Blocked` with explicit blocker codes. The
operation type is a specification, not a command or a credential.

The stable manifest SHA-256 covers version tag, preparation/snapshot/source/
destination IDs, organization, F-026/027/028 evidence digests, security
count, all nodes, dependencies, ref counts and blocker codes.

## PostgreSQL

- `incremental_operation_plans`: versioned records unique on
  `(preparation_id,preflight_sha256)`. Same proof yields the same manifest;
  a different F-028 proof creates a new historical plan without rewriting
  existing data.
- `incremental_operation_plan_steps`: bounded 6–60 rows with
  dependencies and foreign key provenance. Database CHECK permits only
  `Blocked` state.
- `incremental_execution_authorization_attempts`: actor, manifest SHA,
  timestamp and safe denial code. Database CHECK permits only `Denied`;
  **there is no execution permit, executable job or target outbox**.

Read APIs validate the whole DAG, fingerprint and source/target binding.
An old manifest remains readable with `isCurrent=false` after the
preflight expires, source changes or credentials are revalidated.
`readyForExecution` is always false.

## Explicit denied-only authorization

Only a superadmin independent from the enqueue operator, planning reviewer
and manifest creator may call
`POST /api/incremental-preparations/{id}/operation-plans/{planId}/execution-authorization`.
Requires `{expectedManifestSha256,acknowledgeBlockedOperations:true}`.
A valid request results in HTTP **409 Conflict**, audited as `Denied`
with `DestinationWritePermissionUnverified` or `OperationPlanNotCurrent`.
This **cannot** grant GitHub access or execute any operation.

Other protected APIs: `GET /{id}/operation-plans` (latest 20),
`GET /{id}/operation-plans/{planId}` (complete bounded DAG),
`GET /{id}/operation-plans/{planId}/authorization-attempts` (last 100).
The two POST endpoints use the separate incremental-review rate limiter.
All REST error bodies use safe machine-readable codes.

The Italian/English Discovery workbench displays manifest SHA, history,
blocked operations and ref counts, with no execution button.

## Integration tests

The PostgreSQL17 tests cover 600+ changed refs, 12 DAG nodes, SHA
replay and idempotence, stale preflight, append-only versions, same-count
journal tampering, modified dependencies/counters, concurrent API
replicas and independent denied-only authorization.

The ASP.NET/JWT test extends the F-028 preflight scenario and verifies:
invalid proof rejection, successful plan persistence, replay, all
operations blocked, anonymous/admin refusal to authorize, superadmin
audited denial, stale read after connection revalidation and **no new
Azure DevOps/GitHub network calls** during F-029.

.NET10/xUnit, PostgreSQL17, React/Playwright and Terraform CI remain
mandatory. Phase-0 additive DDL is not a production-reviewed migration.

## Future execution boundary

A later phase must introduce **independent live attestation of actual write
permissions and target availability**, new execution-time approval,
idempotent commands, checkpoints, retry fencing, rollback design and
post-write verification. Neither an F-027 planning review nor an F-028
GET-only preflight qualifies as such attestation. No F-029 path performs
or authorizes GitHub writes.

## Additional unit-level invariants

The F-029 business-layer unit tests independently assert deterministic
ordering across different repository enumeration orders, DAG dependency
identity, SHA-256 sensitivity to every operation and proof field,
strict rejection of non-GitMirror history strategies, duplicate targets,
selection sizes above ten and the rule that even all-404 target probes
never establish GitHub write authorization.
