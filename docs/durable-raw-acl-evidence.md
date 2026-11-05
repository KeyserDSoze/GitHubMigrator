# F-022 — Durable Azure DevOps raw ACL evidence

**Scope:** atomic, checkpoint-provenanced **raw Git security ACL/ACE observations**, read-only from Azure DevOps (GET). This is not an authorization evaluation: Azure DevOps group nesting, deny precedence, branch inheritance and directory membership cannot be inferred from individual allow/deny masks. No destination GitHub write, invitation, access grant or approval is introduced.

## Durable acquisition

The existing F-008 leased, fixed-host ACL reader fetches one token scope at a time (project `repoV2/<project>` and Git repository `repoV2/<project>/<repository>`). Its sanitized immutable `security-acl:<sha256(token)[0..40]>` checkpoint contains only allowlisted `token`, `inheritPermissions`, `acesDictionary[descriptor,allow,deny]`. Headers, tokens used for HTTP authentication, and arbitrary provider extension fields are excluded.

F-022 materializes the flat **unique raw ACE** rows into `discovery_acl_observations`. Each carries `job_id`, the canonical `[token, descriptor]` source key, token, descriptor, SHA-256 fingerprint of a canonical JSON `{AllowMask, DenyMask, InheritPermissions}`, and FK provenance (`resource_key`, `page_index`). Primary key `(job_id,source_id)` plus `(job_id,security_token)` index support efficient keyset paging. The existing checkpoint transaction writes 250-row batches, source page, and one `PageSaved` audit event under the same live, expiring lease; a failure rolls them all back.

Exact replays never duplicate audit events. An old checkpoint can backfill normalized observations without another Azure DevOps request; contradictory fingerprints fail before staging. A 1 MiB source-page limit and 30,000-observation guard remain enforced.

## Source and snapshot reconciliation

After Graph identities, groups and membership verification, the F-008 durable worker verifies the full stored ACL evidence while it still owns the live source lease, and **before** Git-ref manifest preparation or F-013 canonical staging.

- PostgreSQL checks the immutable source snapshot and its connection/job binding.
- Raw allowlisted ACL checkpoint ACEs must agree bidirectionally with normalized PostgreSQL observations using `EXCEPT ALL`.
- Observed ACEs must also agree with the snapshot's **distinct raw security token / descriptor / allow / deny / inheritance tuples**. A project token may legitimately be copied into more than one repository's inline ACL list; this deduplication does **not** derive effective inherited permissions.
- ACL source pages must refer only to expected Git project/repository scopes, must use page zero and have no unhandled continuation. For `aclsCollected=true`, all project and repository scopes require a checkpoint even if it contains zero ACEs.
- Equal-count substitutions and missing/extra evidence fail as `AclSnapshotMismatch`; expired leases fail `DiscoveryLeaseLost`. Re-ingestion detects altered fingerprints, potentially returning `AclObservationConflict`.

## Protected read APIs and UI

Existing `PasswordChanged` and admin/superadmin policies:

| Method | Route | Semantics |
| --- | --- | --- |
| GET | `/api/jobs/{jobId}/acl-progress` | Provisional pages and distinct ACE count, without descriptors/masks. |
| GET | `/api/jobs/{jobId}/acls?take=50&after=...` | Keyset-paged (maximum 100) raw allow/deny/inherit evidence **only after job success**. Returns 409 for unfinished jobs and 400 for invalid paging. |

The React Discovery workbench reports provisional ACL collection in IT/EN and labels values as raw ACEs rather than verified effective permissions.

## Verification and next stage

Real PostgreSQL integration cases include >500 ACEs / 250-row batches, parent ACL copied to two repositories, empty ACL pages, canonical fingerprint parity, keyset paging, replay without extra audit events, whole-page rollback on malformed evidence, scope spoofing, equal-count masks swap, missing empty scope, legacy checkpoint backfill, expired lease and HTTP authorization/paging.

**Not yet memory-externalized:** the Azure DevOps security reader still accumulates repository-level ACL evidence and the discovery snapshot retains `aclEvidence` inline, as required for existing F-013 and mapping consumers. F-023 will externalize ACL snapshot storage and stage by indexed bounded source pages; later increments will add reviewed production migrations, delta polling, retention/compaction, scale load tests, approvals and controlled GitHub writes.
