# ADR-007 — Separate security evidence from effective source access

Status: Accepted for the F-003 security evidence slice

## Context

Azure DevOps Git security uses hierarchy token `repoV2/projectId/repositoryId`, Graph descriptor identities, direct group relationships and raw Allow/Deny masks. Neither graph users nor a raw ACL constitutes an effective per-user access decision. Group memberships can be nested or external and the service applies inheritance/deny precedence.

## Decision

- Store typed, provider-neutral raw `DiscoveryGroup`, `DiscoveryMembership` and `RepositoryAclEvidence` records within immutable `DiscoveryInventory` snapshots. Preserve security token, ACE descriptor, Allow/Deny bitmasks, scope and ACL inheritance flag.
- Join Graph principals to security descriptors only through optional `legacyDescriptor`; unresolved principals retain an opaque descriptor. No display-name matching.
- Call fixed Microsoft read-only Group/Membership and Git ACL endpoints, paginated or bounded, using the F-002 secret privately. On failure, record explicit scope/errors/truncation warnings and keep partial results.
- Keep `PermissionsComplete=false`, `EffectivePermissionsVerified=false` and `DiscoverySnapshot.AccessGrants=[]` until a verified evaluator resolves full effective repository access. No GitHub ACL mapping from raw masks.
- Security ACL query requires high-privilege `vso.security_manage` even though this implementation only issues GET. Do not automatically request or expand token scope.
- Security inventory is limited to administrators and remains auditable without logging authorization headers.

## Consequences

- The UI can show provenance and whether an ACE is at project or repository scope without implying user access.
- Groups not visible through Graph, nested/external membership, branch ACLs and inherited allows/denies remain explicit gaps; an empty ACE list is not an "everyone denied" conclusion.
- F-004 needs a new effective access preflight or a reviewed mapping policy, including branch-specific and TFVC permissions where relevant, before publishing access grants.
- Sensitive identity/ACL snapshots require a future retention and data classification policy.
