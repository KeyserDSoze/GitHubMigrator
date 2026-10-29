# ADR-006: Immutable source discovery with explicit completeness

Status: Accepted for F-003 MVP

Context: Azure DevOps has separate REST APIs and permissions for projects, Git, TFVC, Graph identities and repository security. Returning empty lists for inaccessible subsystems would mislead target permission previews.

Decision:
- Implement a provider-neutral `DiscoveryInventory` containing source metadata plus issue codes, `IdentitiesComplete` and `PermissionsComplete`; persist a new immutable version per discovery.
- Keep source PAT exclusively in the F-002 credential store. The discovery use case takes a saved connection ID, not a PAT parameter.
- Use a dedicated Azure DevOps Services adapter with fixed HTTPS hostnames, disabled redirects, bounded pagination, cancellation and per-project error codes.
- Collect projects, Git repos and refs, TFVC branch roots/project-root candidates, and Graph users. Do NOT infer access grants or permission mapping. The initial source inventory always declares itself partial.
- Restrict snapshot reads and scans to admin/superadmin due to personal identity data.

Consequences:
- F-004 can map identities only after effective repository access/ACL inventory is implemented and verified.
- Snapshot JSONB may become large; future storage normalization, pagination and artifact manifests are needed.
- Synchronous discovery has a 90-second deadline; durable workers from F-008 are necessary for larger organizations.
- TFVC branches are not the full versioned tree. A follow-up feature must cover unbranched paths and changeset topology.
- No claim of full coverage is made without evidence; missing Graph scope is shown as an error, not zero users.
