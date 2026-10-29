# GitHubMigrator

An extensible, feature-driven platform to migrate source control and (later) pipelines and work tracking from Azure DevOps Services, Azure DevOps Server / TFS, and other systems to GitHub Enterprise.

**Status: Phase 1, F-002 connections + F-003 read-only discovery + F-004 identity mapping drafts.** Azure DevOps Services and GitHub Enterprise Cloud credential connections can be verified and managed. **F-003 read-only inventory** now discovers projects, Git repositories/refs, TFVC roots, Graph users/groups and direct memberships, plus Git ACL allow/deny evidence from Azure DevOps Services. Effective repository permissions, verified GitHub organization membership, full TFVC history and real migrations are **not yet implemented**. The platform must never claim a migration completed without verification.

## Architecture

```text
Source adapters (Azure DevOps Services / Server / TFS / future)
        ↓ discovery
Canonical migration snapshot + editable migration plan
        ↓ validation / mapping / approval
Migration workers (Git mirror | TFVC conversion)
        ↓
Destination adapters (GitHub Enterprise Cloud / Server / future)
        ↓ verification + immutable report
```

- Backend: .NET 10, DDD, single bounded context, clean ports/adapters, PostgreSQL, Azure Blob Storage.
- Frontend: React 19 + TypeScript + Vite, Italian/English, light/dark theme.
- Local: Docker Compose, PostgreSQL, Azurite, Grafana LGTM (OpenTelemetry).
- Azure: Static Web Apps, App Service container, Container Registry, PostgreSQL Flexible Server, Blob Storage, Key Vault, Application Insights and Foundry.
- Tests: xUnit and Playwright. Feature specs in `features/`, decisions in `ADR/`.

## Quick start

Requirements: Docker Desktop / Docker Engine with Compose v2.

```bash
cp .env.local.example .env.local
./script/dev.sh up
# Or Windows PowerShell:
# Copy-Item .env.local.example .env.local
# ./script/dev.ps1 up
```

Local defaults: UI http://localhost:5173, API http://localhost:8080/api/health, Grafana http://localhost:3000. All published ports are configurable. Copy the example rather than committing real credentials.

Bootstrap (LOCAL TEST ONLY): `superadmin` / `superadmin`; changing it at first login is mandatory. On Azure configure a strong one-time bootstrap password through Key Vault; the insecure default is rejected outside Development.

See [architecture](docs/architecture.md), [local development](docs/local-development.md), [migration fidelity](docs/tfvc-migration.md), [Azure deployment](docs/azure-deployment.md), [ADRs](ADR/) and [roadmap](features/README.md).

## Scope

Phase 1: local platform identities, credential connections (F-002), Azure DevOps Services inventory (F-003), and review-only identity mapping drafts (F-004 MVP) implemented; verified effective access grants, code migration and reports are not yet implemented. Future: pipelines → Actions, boards → GitHub Issues/Projects, guest users, AI-assisted transformations. Until explicitly implemented these features are **planned**, not functional.

## Principles

No access token or plaintext password in the canonical snapshot, application logs, or Git. No automatic permission grants when mappings are missing. Every migration requires a preview, explicit confirmation, resumability and post-migration verification.

## F-002 connections

Sign in, change your bootstrap password, then open **Connections**. Add a source using its Azure DevOps Services *organization name* (not a URL) and a PAT with permission to read projects (`vso.project`/profile as applicable). Or add a target using its GitHub Enterprise Cloud *organization login* and a PAT. The initial GitHub probe authenticates `/user` and checks `/orgs/{org}`, **not the token's repository creation permission**. An explicit write preflight will precede F-006 migration. Only platform superadmin/admin can create, verify, rotate or delete connections. Secrets are never serialized in API responses.

Local development stores PATs in PostgreSQL encrypted with AES-256-GCM using the 32-byte `LOCAL_ENCRYPTION_KEY` from ignored `.env.local`. Use a unique key per environment: losing it makes local PATs unreadable. Azure uses Key Vault with the API managed identity. Neither plain PAT nor encryption key belongs in Git. Current PAT integration is for user-driven development only; for enterprise production authentication prefer Microsoft Entra / GitHub App where feasible.

See [connection API and threat model](docs/connections.md) and [ADR-005](ADR/ADR-005-connection-secrets.md).

## F-003 Azure DevOps discovery

After saving an Azure DevOps Services source connection, open **Discovery** (formerly Repositories) and run a read-only scan. Snapshots are immutable and stored in PostgreSQL. Browse projects, Git repositories and refs, TFVC branch roots, Graph identities and explicit coverage warnings. No source or destination writes occur. See [F-003 documentation](docs/discovery.md).

**PAT scopes:** `vso.project`, `vso.code`, `vso.graph` as permitted. Without Graph permission, the snapshot is partial and shows an error rather than showing "no users" as verified. Effective repository permissions / group inheritance remain unresolved.

### Git repository security evidence

The **Discovery → Groups and ACL evidence** tab shows Azure DevOps Graph groups and direct membership edges alongside project/repository Git ACL masks. The raw evidence is *not* a computed effective-permissions list; unknown descriptors, nested groups, Deny and inheritance must be reviewed. The Graph API needs `vso.graph`; the security ACL API documents **high-privilege `vso.security_manage`** (read *and* write capabilities). Dedicated short-lived credentials are advisable; without the scope the snapshot remains partial. No GitHub permissions are modified or generated by this feature.

## F-004 Identity mapping drafts

After read-only source discovery and creation of a verified GitHub Enterprise Cloud destination connection, open **Identity mapping / Mapping utenti**. Select a snapshot and a destination. Assign source Graph users to explicit GitHub **login names**, leave unresolved users blank, and save a versioned draft. Each snapshot/destination pair has an independent draft. A concurrent edit by a different admin returns HTTP 409 instead of silently overwriting the newer mapping.

The UI displays discovered, currently mapped and unmapped counts, unknown ACL principals and explicit blockers. **A mapped login is not an authenticated GitHub account, a member of the target organization, or a repository grant.** Neither F-004 nor the source ACL snapshot issues invitations or changes permissions. See [F-004 documentation](docs/identity-mapping.md).
