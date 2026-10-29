# Local development

- Install Docker Engine/Desktop with Compose v2. For host-only development use .NET 10 SDK and Node 22+.
- Copy `.env.local.example` to `.env.local` and edit host ports. Never commit `.env.local` or real tokens.
- `./script/dev.sh up` or `./script/dev.ps1 up` runs the stack; `down`, `logs`, `ps` and `test` are also accepted.
- To deliberately run the checked-in test configuration use `./script/dev.sh up test` or `./script/dev.ps1 up test`.
- `.env.test` contains non-secret **local test values** only and is tracked in Git.
- Ports are host-side mappings; Docker service-to-service DNS uses standard container ports (5432, 10000, 4318, 8080).
- UI: `http://localhost:${FRONTEND_PORT}`; API: `http://localhost:${API_PORT}/api/health`.
- Grafana: `http://localhost:${GRAFANA_PORT}`, built-in dev login admin/admin. Logs and traces come through OTel.
- PostgreSQL: localhost:${POSTGRES_PORT}; Azurite Blob: localhost:${AZURITE_BLOB_PORT}.
- First login LOCAL TEST ONLY: superadmin / superadmin, forced password update. Production startup rejects this default.
- The container build restores NuGet packages and npm packages; an internet connection is required on first run.

`test` runs backend xUnit and frontend Playwright checks in containers. Integration tests requiring live Azure DevOps or GitHub credentials are not part of the default test suite.

Known initial limitations: feature adapters for real source discovery, job queue, database migrations and Git push are not yet implemented. The local scaffold is not a finished migrator.
