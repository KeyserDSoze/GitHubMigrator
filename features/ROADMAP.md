# Delivery roadmap — one working vertical feature at a time

**Method:** TDD (failing acceptance/unit tests → implementation → refactoring → CI). Each feature must include domain/API contracts, infrastructure adapter, React UX, i18n IT/EN, observability, tests and a documentation/ADR update where needed. No fake success states for partially implemented providers.

| Order | Feature | Prerequisite | Deliverable | Status |
| --- | --- | --- | --- | --- |
| 0 | F-000/F-001 Foundation and local authentication | – | React/.NET stack, roles, first-login change, Compose/Azure | Initial foundation |
| 1 | **F-002 Secure source/destination connections** | F-001 | Azure DevOps Services/GitHub Enterprise Cloud PAT lifecycle and read probes | **Implemented; live/DB smoke testing pending** |
| 2 | **F-003 Discovery** | F-002 | Read-only project, Git ref, TFVC branch-root and Graph-user snapshots | **MVP plus group/ACL evidence; effective permissions/live integration pending** |
| 3 | F-004 Identity mapping | F-003 | Map GitHub logins, inherited access and orphan warnings | Planned |
| 4 | F-005 Migration plan/preview | F-003/F-004 | Repo selection, renaming, permission/destination previews and approval | **Preview, versioned drafts, target collision probes and destination self-membership/policy review; approval pending** |
| 4.5 | **F-013 Canonical staging and incremental sync** | F-003 | Indexed per-asset PostgreSQL catalog, SHA-256 deltas, idempotent source snapshot sync | **First working slice; provider delta polling and graph pending** |
| 4.6 | **F-014 Incremental ref observations** | F-008/F-013 | Git ref metadata persisted transactionally per page, provisional progress and source hash compatibility | **Per-page source Git ref ingestion, externalized snapshot metadata, immutable 250-ref manifests and chunk-driven canonical staging implemented; other asset families/benchmarks pending** |
| 4.7 | **F-015 Graph identity observations** | F-008/F-013 | Transactional sanitized user-page observations, canonical identity hashes and pre-stage snapshot consistency | **Per-page transactional Graph observations; F-018 now externalizes source-user arrays on leased jobs** |
| 4.8 | **F-016 Paged identity mapping reviewer** | F-004/F-015 | Snapshot-bound PostgreSQL source identity pages, bounded validation/preview and cross-page draft UX | **Paged source identities implemented; F-005 and Graph source externalization now implemented via F-017/F-018** |
| 4.9 | **F-017 F-005 immutable plan evidence** | F-005/F-014/F-016 | Snapshot-bound Git ref counts, checked source-page evidence and identity-draft verification | **Snapshot-bound plan evidence implemented; Graph identity source externalization now implemented via F-018** |
| 4.10 | **F-018 Externalized Graph source identities** | F-015/F-016/F-017 | Bounded Graph identity acquisition + snapshot count + verified chunked canonical staging | **Implemented for leased source jobs; groups, memberships, ACLs, production benchmarks pending** |
| 4.11 | **F-019 Graph security-group observations** | F-003/F-008/F-013 | Leased group-page ingestion, fingerprint checks, snapshot reconciliation and read-only progress | **Implemented first provisional-security-evidence slice; group/membership/ACL externalization pending** |
| 5 | **F-008 Durable job execution** | F-013 (staging); F-005 (future writes) | PostgreSQL leased workers, retries, idempotency and audit | **Staging + Azure DevOps discovery jobs + source/repository/Graph/security-evidence checkpoints implemented; GitHub writes blocked pending approval** |
| 6 | F-006 Native Git migration | F-005/F-008 | Mirror branches, tags and history; compare refs/SHAs | Planned |
| 7 | F-007 TFVC conversion | F-005/F-008 | Best-effort changesets/branches conversion and evidence report | Research |
| 8 | F-012 Azure DevOps Server/TFS and GitHub Enterprise Server | F-002/F-003 | Version-specific adapters and safe custom endpoints | Planned |
| 9 | F-009 Pipeline → Actions | F-002/F-003 | Read-only YAML task analysis + AI advisory + safe disabled draft; AI model connections, redaction, output gates (actionlint/zizmor/policy/task accounting) an AI conversion *proposal*, its persistence and the human approval bound to the workflow hash built; PR publication pending | **Preview MVP; proposal and approval built, PR publication pending; deployment pending** |
| 10 | F-010 Boards → Issues/Projects | F-002/F-003 | Issue draft with source ID, direct relations, target GitHub existence checks; later idempotent issue write | **Preview and reconciliation MVP; publishing pending** |
| 11 | F-011 Guest/external users | F-004/F-005 | Guest governance, invitations and reviews | Later |

## Product boundaries

- Platform roles **superadmin/admin/user** control the migrator, while mapped source users and GitHub principals are a different aggregate.
- Source providers must declare capabilities. Discovery into the canonical snapshot happens **before** destination mapping.
- A migration is not successful merely because Git push succeeded: refs and access assignments must be verified.
- A source project can contain multiple Git repositories or TFVC paths. Migrate selected **repositories**, not blindly one Git repo per project.
- Preserve Git refs/branches/tags wherever possible; do not promise identical TFVC merge graph or label semantics.
- Secret storage, rate limiting, provider isolation, redaction, human approval and logging/traces are cross-cutting requirements.
- AI conversion can suggest transformations, not approve them or expose secrets; Foundry model availability depends on quota and region.

## Next: F-005 migration plan delivery slices

1. **Implemented:** persist versioned per-snapshot/destination draft selections with compare-and-swap concurrency.
2. **Partial:** GET-only target probes detect visible collisions and self-membership/org policy evidence; 404 remains ambiguous. Verify token creation rights, mapped-user memberships and end-to-end target availability before approval.
3. Validate effective source access and permission intent without inferring GitHub grants from ACL masks or mapped logins.
4. Explicitly approve an immutable, validated plan version before F-008 workers may execute it.
5. Add live provider smoke tests, HTTP integration and Playwright coverage for draft concurrency, preflight and approvals.

## Historical F-003 discovery implementation slices

1. Azure DevOps Services projects list with paging and cancellation, using saved F-002 connection references.
2. Project Git repositories, branch inventory and default branches.
3. TFVC path discovery and supported vs unsupported capabilities.
4. Identity and membership collection with group/effective permission evidence and safe pagination.
5. Immutable snapshot metadata in PostgreSQL and large source manifests in Blob Storage; retry and progress trace IDs.
6. React inventory and project selection with loading, empty, error and partially discovered states.
7. Mocked HTTP unit tests, deterministic domain tests, endpoint tests, Playwright and optional live-provider smoke.

**Definition of Done:** all acceptance criteria covered, CI green, no PATs/secret references in external responses or logs, supported permissions documented, backward-compatible schema handled, and an operator can complete the intended feature end-to-end.

## Staging-driven migration engine

F-013 normalizes F-003 discovery snapshots into a revisioned, queryable PostgreSQL catalog. See [staging-sync.md](../docs/staging-sync.md). Next: source API cursor/polling, dependency graph, pipeline/Boards staging, F-008 PostgreSQL staging + Azure DevOps discovery jobs, page checkpoints, artifact storage and approved GitHub writes with reconciliation. Staging is not an approval or successful migration.
