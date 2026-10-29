# Delivery roadmap — one working vertical feature at a time

**Method:** TDD (failing acceptance/unit tests → implementation → refactoring → CI). Each feature must include domain/API contracts, infrastructure adapter, React UX, i18n IT/EN, observability, tests and a documentation/ADR update where needed. No fake success states for partially implemented providers.

| Order | Feature | Prerequisite | Deliverable | Status |
| --- | --- | --- | --- | --- |
| 0 | F-000/F-001 Foundation and local authentication | – | React/.NET stack, roles, first-login change, Compose/Azure | Initial foundation |
| 1 | **F-002 Secure source/destination connections** | F-001 | Azure DevOps Services/GitHub Enterprise Cloud PAT lifecycle and read probes | **Implemented; live/DB smoke testing pending** |
| 2 | **F-003 Discovery** | F-002 | Read-only project, Git ref, TFVC branch-root and Graph-user snapshots | **MVP implemented; permissions/integration pending** |
| 3 | F-004 Identity mapping | F-003 | Map GitHub logins, inherited access and orphan warnings | Planned |
| 4 | F-005 Migration plan/preview | F-003/F-004 | Repo selection, renaming, permission/destination previews and approval | Planned |
| 5 | F-008 Durable job execution | F-005 | Workers, retries, checkpoints, progress, audit and verification | Planned |
| 6 | F-006 Native Git migration | F-005/F-008 | Mirror branches, tags and history; compare refs/SHAs | Planned |
| 7 | F-007 TFVC conversion | F-005/F-008 | Best-effort changesets/branches conversion and evidence report | Research |
| 8 | F-012 Azure DevOps Server/TFS and GitHub Enterprise Server | F-002/F-003 | Version-specific adapters and safe custom endpoints | Planned |
| 9 | F-009 Pipeline → Actions | F-005/F-008 | Rule-based conversion + Foundry AI suggestions + human approval | Later |
| 10 | F-010 Boards → Issues/Projects | F-003/F-008 | Work item hierarchy and metadata mapping | Later |
| 11 | F-011 Guest/external users | F-004/F-005 | Guest governance, invitations and reviews | Later |

## Product boundaries

- Platform roles **superadmin/admin/user** control the migrator, while mapped source users and GitHub principals are a different aggregate.
- Source providers must declare capabilities. Discovery into the canonical snapshot happens **before** destination mapping.
- A migration is not successful merely because Git push succeeded: refs and access assignments must be verified.
- A source project can contain multiple Git repositories or TFVC paths. Migrate selected **repositories**, not blindly one Git repo per project.
- Preserve Git refs/branches/tags wherever possible; do not promise identical TFVC merge graph or label semantics.
- Secret storage, rate limiting, provider isolation, redaction, human approval and logging/traces are cross-cutting requirements.
- AI conversion can suggest transformations, not approve them or expose secrets; Foundry model availability depends on quota and region.

## Next: F-003 discovery implementation slices

1. Azure DevOps Services projects list with paging and cancellation, using saved F-002 connection references.
2. Project Git repositories, branch inventory and default branches.
3. TFVC path discovery and supported vs unsupported capabilities.
4. Identity and membership collection with group/effective permission evidence and safe pagination.
5. Immutable snapshot metadata in PostgreSQL and large source manifests in Blob Storage; retry and progress trace IDs.
6. React inventory and project selection with loading, empty, error and partially discovered states.
7. Mocked HTTP unit tests, deterministic domain tests, endpoint tests, Playwright and optional live-provider smoke.

**Definition of Done:** all acceptance criteria covered, CI green, no PATs/secret references in external responses or logs, supported permissions documented, backward-compatible schema handled, and an operator can complete the intended feature end-to-end.
