# Feature-driven roadmap

All items start with acceptance criteria and automated tests (TDD: red → green → refactor).

| ID | Feature | State |
| --- | --- | --- |
| F-000 | Monorepo, local stack and Azure skeleton | Foundation implemented |
| F-001 | Local identity, first-login password reset, RBAC | Initial implementation |
| F-002 | Provider-neutral connections and secure PAT lifecycle | Implemented for Azure DevOps Services + GitHub Enterprise Cloud; integration smoke pending |
| F-003 | Discovery projects, Git/TFVC, Graph users/groups/memberships and Git ACL evidence | **Read-only evidence implemented**; effective permission validation pending |
| F-004 | Per-snapshot GitHub identity mapping drafts and coverage preview | **Initial review-only implementation**; account/membership verification and effective grants pending |
| F-005 | Select projects/repos, rename target repos, preview permissions | Planned |
| F-006 | Mirror Git history (all refs/branches/tags) and verify | Planned |
| F-007 | Convert TFVC history with explicit fidelity report | Planned / research |
| F-008 | Durable migration workers, resumable stages, audit reports | Planned |
| F-009 | Read Azure Pipelines, inventory tasks, generate disabled Actions proposal and AI checklist | **Preview MVP implemented**; true conversion/publish pending |
| F-010 | Boards work-item issue drafts + ID-specific GitHub object reconciliation | **Review MVP implemented**; issue creation and link patching pending |
| F-011 | Guest user handling and governance | Later |
