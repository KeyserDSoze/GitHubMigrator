# Local development

Prerequisites: Docker Desktop (Windows PowerShell) or Docker Engine + Compose v2 (Linux). For direct development use .NET 10 SDK and Node 22+.

Copy `.env.local.example` to `.env.local`; edit `HOST_BIND_ADDRESS`, `PUBLIC_HOSTNAME` (when accessing a remote Linux host), and any `*_PORT` values. Avoid publishing local development services on a public network. **Never commit .env.local or live tokens.**

Commands:
- Linux: `./script/dev.sh up`, `down`, `logs`, `ps`, `test`, `reset`.
- Windows PowerShell: `./script/dev.ps1 up`, `down`, `logs`, `ps`, `test`, `reset`.
- Pass `test` as SECOND argument to any command to use the tracked `.env.test` (e.g. `./script/dev.sh up test`). Default uses `.env.local` if it exists, otherwise test settings.
- `reset` **deletes all local database, Azurite and telemetry volumes**.

Ports are host-side mappings; Compose-internal services use constant ports. Endpoints by default: UI http://localhost:5173; API http://localhost:8080/api/health; Grafana http://localhost:3000 (admin/admin in test settings); PostgreSQL localhost:5432; Azurite 10000/10001/10002; OTLP 4317/4318.

The API startup creates schema via `EnsureCreated` **for phase-0 bootstrap only**. Migrations must replace this before production. The local API seeds superadmin/superadmin *only in Development* and forces a password change at first login. Changing .env credentials does not alter an existing seeded account: reset the dev volume when appropriate. No provider PATs are required for the foundation.

The Compose `test` action builds/runs xUnit and Playwright in isolated test containers. Playwright API calls are mocked in UI contract tests. The app starts without any real Azure DevOps/GitHub integration: source discovery and migration are not yet implemented.

First container build pulls base images and NuGet/npm dependencies (internet access required).

### F-002 connections

Use `LOCAL_ENCRYPTION_KEY` (base64, 32 bytes) in `.env.local`; the example/test key is **public and not safe outside local development**. Start the stack, log in, change the bootstrap password, and open Connections. First test a PAT without storing it, then save it. The Azure DevOps Services source requires an organization name and sufficient read-only project permissions; GitHub Enterprise Cloud requires an organization login. PATs are encrypted in local PostgreSQL, or stored in Key Vault when deployed on Azure. No live tokens are needed for xUnit and mocked Playwright tests. Test against live APIs manually using dedicated throwaway PATs.
