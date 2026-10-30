# Integration and quality test strategy

**Status:** HTTP-contract mock tests, browser mock journeys, dedicated PostgreSQL 17 integration suite, coverage artifacts and browser failure traces run in GitHub Actions. No live Azure DevOps or GitHub account is required for the deterministic suites.

## Four independent levels

| Layer | Tool | Runs what is real? | What is simulated? |
| --- | --- | --- | --- |
| Domain | xUnit | Planner, mapping validation, YAML task parser, safety rules | Data objects |
| HTTP integration | xUnit + `HttpMessageHandler` | Azure DevOps and GitHub REST adapters, HTTP request construction, preview orchestration, AI-safe payload | Only outbound provider responses and the AI result |
| Persistence integration | xUnit + actual PostgreSQL 17 | EF Core/Npgsql model, JSONB persistence, SQL compare-and-swap revision updates | No source/destination API; short-lived PostgreSQL container |
| UI journeys | Playwright + `page.route()` | React browser UI, navigation, requests, rendering, error handling | Backend JSON and authentication response |

The wire-mock tests exercise the **production readers** `AzureDevOpsWorkloadReader`, `GitHubConfigurationReader` and `GitHubTraceabilityReader` rather than replacing these with mock services. Contract fixtures include:

- Azure Pipelines YAML, variable groups and project-level service connections; GitHub secret/variable list responses and pagination
- Secret-value suppression in serialized output **and** the AI review payload
- 401 / 403 / 429 / 302, inaccessible source YAML, unreadable GitHub config
- Azure Boards work-item hierarchy and direct commit/PR artifacts, GitHub 200/404/403 target checks
- Cases where a target object exists but **no GitHub Issue relationship has been created**
- UI invalid SHA and duplicate work-item IDs, admin vs regular user role access, backend error visibility

The PostgreSQL suite tests a real database created by EF Core on an ephemeral GitHub Actions service container: snapshot/destination isolation, JSONB roundtrip including quoted Unicode descriptors, stale revision conflict and distinct readers/writers. **It must fail when the database is unavailable**, not silently use EF InMemory.

## Commands

From the repository root:

```bash
# Unit + provider-wire tests (no external services)
dotnet test src/backend/GitHubMigrator.slnx --collect:"XPlat Code Coverage"

# Browser UI with mocked API endpoints
cd src/frontend
npm install
npm run build
cd ../../tests/frontend
npm install
npx playwright install chromium
npx playwright test

# PostgreSQL integration (Docker-backed database; sample local-only credentials)
docker run --rm -d --name githubmigrator-test-pg -p 5432:5432 \
  -e POSTGRES_USER=migrator_test -e POSTGRES_PASSWORD=ci-local-only-password \
  -e POSTGRES_DB=migrator_tests postgres:17
export GITHUBMIGRATOR_TEST_POSTGRES='Host=localhost;Port=5432;Database=migrator_tests;Username=migrator_test;Password=ci-local-only-password;Pooling=false'
dotnet test tests/integration/GitHubMigrator.Integration.Tests.csproj --collect:"XPlat Code Coverage"
docker rm -f githubmigrator-test-pg
```

The same tests run on Linux GitHub Actions. On Windows, run the equivalent Docker command and set the connection string with `$env:GITHUBMIGRATOR_TEST_POSTGRES = '...'` in PowerShell.

## GitHub Actions and diagnostics

The `Quality gates` workflow now has **four independent jobs**: .NET/xUnit, React/Playwright, Terraform/Docker checks, and PostgreSQL 17 integration. The .NET jobs collect Cobertura reports (`coverage.cobertura.xml`) and upload them as CI artifacts. Playwright keeps trace and screenshot evidence for failing scenarios and uploads them on failures.

**Important:** more test cases do not establish complete source-to-target migration correctness. These tests do not prove behavior against every Azure DevOps Services/Server version or GitHub enterprise permission variant. Before the first production release add: authenticated ASP.NET endpoint integration tests, a full migration rehearsal using disposable source and destination organizations, permission/service-connection scopes, durable-worker retry/crash tests, data retention and destruction tests, and reconciliation of Git/TFVC code references after migration. Coverage reports give a baseline; no arbitrary high coverage percentage is claimed.

The deterministic mocks deliberately forbid HTTP methods other than GET on provider APIs and verify that PATs cannot appear in URL paths, output JSON or model prompts. They do not create secrets, issues, actions or approvals.
