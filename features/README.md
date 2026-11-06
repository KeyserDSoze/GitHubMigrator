# Feature-driven roadmap

All items start with acceptance criteria and automated tests (TDD: red → green → refactor).

| ID | Feature | State |
| --- | --- | --- |
| F-000 | Monorepo, local stack and Azure skeleton | Foundation implemented |
| F-001 | Local identity, first-login password reset, RBAC | Initial implementation |
| F-002 | Provider-neutral connections and secure PAT lifecycle | Implemented for Azure DevOps Services + GitHub Enterprise Cloud; integration smoke pending |
| F-003 | Discovery projects, Git/TFVC, Graph users/groups/memberships and Git ACL evidence | **Read-only evidence implemented**; effective permission validation pending |
| F-004 | Per-snapshot GitHub identity mapping drafts and coverage preview | **Initial review-only implementation**; account/membership verification and effective grants pending |
| F-005 | Select repositories, rename GitHub targets and review safety blockers | **Preview, versioned drafts, GET-only target existence and destination self-membership/policy review implemented**; token create rights and approval pending |
| F-006 | Mirror Git history (all refs/branches/tags) and verify | Execution pending; F-030 adds only a read-only branch/tag OID comparison core |
| F-030 | Deterministic, provenance-bound Git branch/tag reconciliation | Business logic + xUnit implemented; not an executor |
| F-031 | Live GitHub branch/tag ref evidence with immutable PostgreSQL proof | GET-only adapter + audit/paged API implemented; destination writes blocked |
| F-032 | Snapshot-bound Azure DevOps ↔ GitHub branch/tag reconciliation | F-014/F-031 proofs joined in immutable PostgreSQL, compared by F-030, HTTP history/paging; no writes |
| F-033 | Read-only React Git ref evidence and reconciliation workbench | Admin UI for F-029/F-031/F-032, safe capture/compare, immutable history, paginated decisions and Playwright tests; no execution |
| F-034 | Durable, resumable Git transfer preparation workpacks | PostgreSQL lease-fenced evidence checkpoints, audit events, hard denied write barrier; no Git executor |
| F-035 | Offline Git source object export to checksum-bound bundles | Real local-only Git CLI, exact F-014/F-032 ref proof, fsck, bounded Git bundle, PostgreSQL immutable SHA-256; no GitHub writes |
| F-036 | Read-only authenticated Azure DevOps Services Git acquisition | Fixed-host HTTPS Git fetch with scoped source credentials, bounded bare mirrors, sealed source-ref verification and F-035 bundle proof; no GitHub writes |
| F-037 | Durable async Azure Git acquisition queue | PostgreSQL cross-replica two-slot leases, verified mirror/bundle checkpoints, restart recovery, safe audit events and polling API; no GitHub writes |
| F-038 | Physical Git bundle integrity, orphan recovery and bounded local storage | Rehash on reuse, verify/promote orphan bundles without overwrite, per-root quota locking, conservative stale-temp cleanup and tests; no GitHub writes |
| F-039 | GitHub destination import-readiness evidence (GET-only) | Exact F-032/F-038 SHA-binding, visible destination + advisory reported push, immutable PostgreSQL blockers and no-write SQL CHECK; no GitHub import |
| F-040 | GET-only policy inspection and two-person Git import intent | Inherited ruleset/default-branch protection categories, exact create-only ref plan SHA, 10-minute second-superadmin acknowledgement, immutable PG ledger and permanent execution-denied CHECK |
| F-041 | Verified offline Git create-only import rehearsal | Actual local bare Git clone/push --atomic + absent-ref leases, on-disk SHA-256/fsck, no GitHub writes, isolated scratch and PostgreSQL proof |
| F-042 | Fresh GitHub target ref drift fence | GET-only complete ref/OID/type comparison to immutable F-031 baseline, 30-second SHA-sealed PostgreSQL history, no write authorization |
| F-043 | GitHub App single-repository Contents:write credential attestation | Disabled-by-default RSA/JWT GitHub App token issuance scoped to one allowlisted repository, minted token immediately discarded, source-locked PostgreSQL receipt and SQL deny-execution constraints; no repository writes |
| F-044 | Sealed create-only execution candidate packet | SHA-256-verified, ref-limited (max 128) read-only derivation of exact missing refs from F-032/F-041/F-042/F-043, physical bundle rehash before/after, superadmin-only GET, permanently blocked execution; no GitHub push |
| F-045 | Distributed GitHub target preview leases | Off-by-default 5–20s PostgreSQL reservation per global GitHub numeric repository ID; cross-replica advisory locking, generation/token CAS, expiry quarantine, two-human fresh-proof recovery, SQL deny-write flags; no remote writes |
| F-046 | GitHub GET-only candidate-specific policy and remote outcome guard | Numeric repository metadata identity, inherited tag/push + per-nonexistent-branch active rules (max 16 candidates, 4 concurrent GET), SHA-bound review-only evidence; ambiguous Git transport outcomes prohibit blind retry; no GitHub remote writes |
| F-047 | Durable GitHub transfer preparation and hard write barrier | Off-by-default SHA-bound F-044/F-046 + F-045 lease journal; two distinct superadmin actors, unique active repo slot, cross-replica worker CAS, append-only audit and PostgreSQL transport/write deny constraints; never pushes to GitHub |
| F-007 | Convert TFVC history with explicit fidelity report | Planned / research |
| F-008 | Durable migration workers, resumable stages, audit reports | Planned |
| F-009 | Read Azure Pipelines, inventory tasks, generate disabled Actions proposal and AI checklist | **Preview MVP implemented**; true conversion/publish pending |
| F-010 | Boards work-item issue drafts + ID-specific GitHub object reconciliation | **Review MVP implemented**; issue creation and link patching pending |
| F-011 | Guest user handling and governance | Later |
