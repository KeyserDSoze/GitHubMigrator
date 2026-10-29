# Architecture and bounded context

GitHubMigrator is a **modular monolith** with one domain: Migration. The `applications/Api` host supplies HTTP, authentication and composition root. `business/` supplies use cases and ports; `domain/` supplies entities, invariants and value objects; `infrastructures/` supplies implementations. The domain NEVER imports EF Core, cloud SDKs or HTTP clients.

## Canonical snapshot vs migration plan

A *snapshot* is the immutable inventory fetched for a particular source connection, scan ID and time. A *plan* records the user's editable selections, GitHub naming, source-to-target identity mappings, permission overrides, branch strategy, validation results and target organization. Persist normalized metadata in PostgreSQL. Large files, reports, manifests and migration artifacts go to blob storage using immutable, per-job keys. Git commits themselves live in Git repositories/workspaces, **not in database JSON**.

```text
MigrationWorkspace
  SourceConnectionReference / DestinationConnectionReference
  DiscoverySnapshot { sourceSystem, collectedAt, projects, repos, identities, accessGrants }
  MigrationPlan { projects[], repositories[], identityMappings[], permissionOverrides[] }
  MigrationRun { stages, checkpoints, errors, evidence, verificationReport }
```

Source providers implement `ISourceProvider` (capabilities, inventory, repository artifact export). Destination providers implement `IDestinationProvider` (capabilities, repository provisioning, permissions and verification). Separate TFVC conversion from Git mirror to retain correctness. Never require every provider to support TFVC; surface explicit capabilities and unsupported data.

One Azure DevOps *project* may have **several Git repositories**, while TFVC can contain multiple branches or paths. Destination repository names are mapped individually; a project is not assumed to equal one repository.

## Planned flow

1. Connect with credentials stored as references to secrets (never return PAT to UI).
2. Discover projects, repos (Git vs TFVC), identities, groups, effective permissions, branch metadata.
3. Create snapshot and editable mapping; mark unmapped / orphaned access as warnings.
4. Preview target repos and proposed access, including group inheritance and guest identities.
5. Validate source/target permissions, ref conflicts, LFS, repository sizes, history conversion strategy.
6. Approve and queue an idempotent run; stream stage events and structured logs.
7. Mirror Git refs or convert TFVC changesets using an isolated worker; upload and verify.
8. Issue a per-repo migration report with ref coverage, commit counts and identity/permission exceptions.

## Runtime

API serves short-lived HTTP requests. Actual migrations MUST run in separate durable background workers; do not clone entire repos inside requests. The future worker can use a persistent queue, leases, heartbeat and checkpoints. Initial Docker Compose/API is **foundation only**.

## Security

Local users (superadmin, admin, user) are **platform identities**, not the Azure DevOps/GitHub identities being mapped. Only superadmin/admin provision users. Passwords hashed, first-login change required. Default test credential allowed only in Development. An admin must not implicitly acquire GitHub organization administration rights. PAT scopes should be least privilege; migration secrets will be encrypted at rest and referenced, never logged.

## Observability

Use structured `ILogger` messages with migrationRunId, sourceSystem, projectId, repoId, stage, duration, correlation/traceId, outcome, failureCode. Exclude PATs, bearer headers, credential URLs, raw secret payloads, personally sensitive user data. OpenTelemetry routes local telemetry to Grafana LGTM, Azure Monitor on cloud.

## F-002 implemented connection boundary

`ConnectionService` in Business owns provider-neutral connection orchestration and defines `IConnectionValidator`, `IConnectionRepository` and `IConnectionSecretStore`. Azure DevOps Services and GitHub Enterprise Cloud validators are separate infrastructure projects. The PostgreSQL adapter stores only connection metadata; the secret adapter is selected by DI (`EncryptedPostgreSql` in Development, Key Vault in Azure). The React Connections page exposes test, save, re-test, rotate and delete actions and **does not** persist PATs in browser storage.

Server/collection URLs and GitHub Enterprise Server custom domains remain unsupported; arbitrary hostnames are intentionally rejected until safe connectivity controls and per-server authentication are designed.

## F-003 discovery boundary

`DiscoveryService` accepts a **saved source connection ID**, reads the secret exclusively through `IConnectionSecretStore`, chooses an `IDiscoveryReader` adapter and saves an immutable `DiscoveryInventory` using `IDiscoveryRepository`. The Azure DevOps Services reader calls fixed Microsoft hosts with read-only methods and bounded paging; the domain model is provider-neutral. The stored snapshot carries immutable source metadata and explicit `DiscoveryIssue` warnings for Graph scope failures, partial TFVC inventory, unknown project source control types and permissions not collected. The synchronous API has a request timeout; resilient background execution will follow with F-008.
