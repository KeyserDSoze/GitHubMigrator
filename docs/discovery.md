# F-003 — Read-only discovery (initial vertical slice)

**Current status:** source inventory and raw Git repository security evidence implemented for **Azure DevOps Services**. No live-PAT integration has been executed from this environment. Azure DevOps Server/TFS on-prem, GitHub migration and effective permission/ACL collection remain future work.

## User journey

1. Log in with an admin/superadmin account whose temporary password is changed.
2. In **Connections**, create an Azure DevOps Services source with organization ID and PAT.
3. In **Discovery**, select this saved source and choose **Run discovery**.
4. The API reads Microsoft-hosted REST endpoints, captures Graph groups/direct memberships and Git repository ACL masks, creates an immutable snapshot in PostgreSQL and returns its summary.
5. Browse the snapshot's projects/repositories, Git ref names + SHA/objectId, TFVC roots, users, **groups and ACL evidence**, plus coverage warnings. Re-scan creates a new snapshot without overwriting old ones.

The source PAT never appears in the snapshot, response, frontend localStorage or logs. No source or GitHub write request is performed.

## API

All routes require authenticated **admin/superadmin** with `password_changed=true`.

| Method | Path | Result |
| --- | --- | --- |
| POST | `/api/discoveries` body `{ "connectionId": "uuid" }` | 201 summary of a new immutable inventory |
| GET | `/api/discoveries` | latest 25 inventory summaries |
| GET | `/api/discoveries/{id}` | canonical inventory with projects, repos, refs, identities and issues |

The POST is synchronous in this initial implementation; a 90-second API deadline bounds the operation. F-008 will provide queued/resumable workers for large organizations.

## Azure DevOps APIs and authentication

- `GET https://dev.azure.com/{org}/_apis/projects?$top=100&$skip=N&api-version=7.1` — project pages; `vso.project`. Continuation header handled where present.
- `GET .../_apis/projects/{id}?includeCapabilities=true&api-version=7.1` — project source control type, including TFVC.
- `GET .../{projectId}/_apis/git/repositories?api-version=7.1` — Git repos.
- `GET .../{projectId}/_apis/git/repositories/{repoId}/refs?$top=1000&api-version=7.1` — all available refs, pagination via continuation; `vso.code`.
- `GET .../{projectId}/_apis/tfvc/branches?includeChildren=true&api-version=7.1` — TFVC branch roots and children, not complete item/changeset inventory; `vso.code`.
- `GET https://vssps.dev.azure.com/{org}/_apis/graph/users?api-version=7.1-preview.1` — Graph user descriptors, display names and email when provided, paginated via X-MS-ContinuationToken; `vso.graph`.

**Effective permissions NOT verified.** Graph users, direct group memberships and raw project/repository ACLs are evidence, not proof of each individual's access. Group membership, ACLs, inherited grants and guest identity semantics are deliberately deferred. Snapshots always have `PermissionsComplete=false` and `PermissionsNotCollected`.

Other limitations: TFVC root branches do not cover all versioned folders or changesets; we include the project root as a candidate rather than a verified Git repository. Refs and projects are read with explicit bounded page/record limits; hitting them produces truncation warnings. API token validation only verifies one read scope and does not prove full discovery coverage. Some API failures become issues on partial snapshots. Failure to obtain the root project list rejects the scan instead of saving misleading empty data.

## Persistence and safety

- `DiscoveryInventory` is the canonical projection: provider key, connection ID, snapshot ID and timestamp, projects/repositories, Git refs, identities, issues, completeness flags. No connection secret reference.
- `migration_discoveries.inventory` is JSONB with a stable per-snapshot primary key, plus connection ID and collected time. Its schema is currently created through phase-0 additive SQL; replace it with reviewed EF migrations before production.
- The source PAT is loaded from the encrypted local PostgreSQL secret store or Azure Key Vault and sent only in HTTPS authorization headers to **fixed** Microsoft domains. HTTP redirects and cookies are disabled.
- Structured logs include only inventory/connection IDs and collection counts, not headers, PATs or provider response content.
- API responses containing user names/emails are restricted to platform administrators, and should later receive configurable data retention.
- CI runs deterministic xUnit HTTP adapter tests and mocked Playwright UI tests. Live Azure DevOps smoke, large-org tests and Docker-backed Postgres integration remain to be executed.

## Microsoft REST references

- https://learn.microsoft.com/en-us/rest/api/azure/devops/core/projects/list?view=azure-devops-rest-7.1
- https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories/list?view=azure-devops-rest-7.1
- https://learn.microsoft.com/en-us/rest/api/azure/devops/git/refs/list?view=azure-devops-rest-7.1
- https://learn.microsoft.com/en-us/rest/api/azure/devops/tfvc/branches/get-branches?view=azure-devops-rest-7.1
- https://learn.microsoft.com/en-us/rest/api/azure/devops/graph/users/list?view=azure-devops-rest-7.1

## Security inventory — F-003 extension

The read-only adapter now gathers three additional datasets alongside Git/TFVC metadata:

1. `GET https://vssps.dev.azure.com/{org}/_apis/graph/groups?api-version=7.1-preview.1`, following `X-MS-ContinuationToken` — group graph descriptor, display name and optional `legacyDescriptor`.
2. `GET https://vssps.dev.azure.com/{org}/_apis/graph/Memberships/{groupDescriptor}?direction=down&depth=1&api-version=7.1-preview.1` — *direct*, not transitive, group membership edges. Nested groups remain distinct nodes. Requests are bounded; missing scope produces an incomplete coverage status.
3. `GET https://dev.azure.com/{org}/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87?token=repoV2/{projectId}[/{repoId}]&api-version=7.1` — separate project and repository Git ACLs, preserving token, legacy principal descriptor, raw `allow`/`deny` bitmask and `inheritPermissions`. Only an exact matching token is accepted. The namespace's `GenericRead` bit is **2**; `GenericContribute` is **4**. No POST, grant or mutation is performed.

**Azure DevOps PAT requirements:** Graph group/membership reads require `vso.graph`. The security ACL query declares the **high-privilege** `vso.security_manage` scope, which can also change permissions through other endpoints. Use a dedicated least-duration PAT under an authorized administrator's control; do **not** enable extra scope casually. If unavailable, the app still collects source metadata and marks ACL coverage incomplete rather than claiming an empty permissions inventory. See the Microsoft [Access Control Lists Query](https://learn.microsoft.com/en-us/rest/api/azure/devops/security/access-control-lists/query?view=azure-devops-rest-7.1) and [Graph Memberships List](https://learn.microsoft.com/en-us/rest/api/azure/devops/graph/memberships/list?view=azure-devops-rest-7.1) documentation.

### Critical security interpretation

- `DiscoveryInventory.groups`, `memberships`, `aclEvidence` and `securityCoverage` are **raw evidence**, never target access assignments. Historical F-003 snapshots deserialize these optional properties as empty/unverified.
- An ACL for a project may be inherited by a repository; it is shown with `scope=project` on each repository. `inheritPermissions` is **the ACL token's property**, not proof of effective inheritance on an individual user.
- `legacyDescriptor` links an ACE identity to a Graph user/group **only when the provider returns that legacy descriptor**. If absent, preserve the unresolved security descriptor; never match by display name.
- `PermissionsComplete=false` and `SecurityCoverage.EffectivePermissionsVerified=false` deliberately remain set. Azure DevOps effective rights can depend on group nesting, directory membership not visible in Graph, inheritance, Deny precedence, branch-level ACLs and externally granted permissions.
- `DiscoverySnapshot.AccessGrants` stays empty. The migration planner emits `PermissionsNotCollected` instead of `OrphanedAccess` when that effective access inventory is incomplete. **No GitHub grants can be inferred from these ACLs**.
- Large organizations currently require queued workers (F-008). The reader caps pages, groups, direct membership edges, repository scans and ACE counts, and emits coverage/truncation issue codes rather than silently ignoring failures. A single scan remains synchronous and is bounded by the API's 90-second deadline.
- Access to the full inventory and identity/ACL evidence remains admin/superadmin-only, and REST requests are GET-only with fixed Microsoft hosts.

The next slice is a dedicated, externally verified effective-permissions evaluator (possibly Azure DevOps Security permission evaluation API) and the completeness criteria for F-004 mapping. Do not merge raw group masks into a GitHub collaborator list without that validation.
