# GitHubMigrator

An extensible, feature-driven platform to migrate source control and (later) pipelines and work tracking from Azure DevOps Services, Azure DevOps Server / TFS, and other systems to GitHub Enterprise.

**Status: Phase 1, F-002 connection management.** Azure DevOps Services and GitHub Enterprise Cloud credential connections can be verified and managed. Source discovery, TFVC conversion and real migrations are **not yet implemented**. The platform must never claim a migration completed without verification.

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

Phase 1: identities and basic credential connections (F-002 implemented); discovery, source-to-target mappings, code migration and reports (not implemented). Future: pipelines → Actions, boards → GitHub Issues/Projects, guest users, AI-assisted transformations. Until explicitly implemented these features are **planned**, not functional.

## Principles

No access token or plaintext password in the canonical snapshot, application logs, or Git. No automatic permission grants when mappings are missing. Every migration requires a preview, explicit confirmation, resumability and post-migration verification.

## F-002 connections

Sign in, change your bootstrap password, then open **Connections**. Add a source using its Azure DevOps Services *organization name* (not a URL) and a PAT with permission to read projects (`vso.project`/profile as applicable). Or add a target using its GitHub Enterprise Cloud *organization login* and a PAT. The initial GitHub probe authenticates `/user` and checks `/orgs/{org}`, **not the token's repository creation permission**. An explicit write preflight will precede F-006 migration. Only platform superadmin/admin can create, verify, rotate or delete connections. Secrets are never serialized in API responses.

Local development stores PATs in PostgreSQL encrypted with AES-256-GCM using the 32-byte `LOCAL_ENCRYPTION_KEY` from ignored `.env.local`. Use a unique key per environment: losing it makes local PATs unreadable. Azure uses Key Vault with the API managed identity. Neither plain PAT nor encryption key belongs in Git. Current PAT integration is for user-driven development only; for enterprise production authentication prefer Microsoft Entra / GitHub App where feasible.

See [connection API and threat model](docs/connections.md) and [ADR-005](ADR/ADR-005-connection-secrets.md).
