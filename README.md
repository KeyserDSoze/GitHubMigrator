# GitHubMigrator

An extensible, feature-driven platform to migrate source control and (later) pipelines and work tracking from Azure DevOps Services, Azure DevOps Server / TFS, and other systems to GitHub Enterprise.

**Status: Phase 1, F-002 connections + F-003 read-only discovery + F-004 identity mapping drafts + F-005 read-only plan preview.** Azure DevOps Services and GitHub Enterprise Cloud credential connections can be verified and managed. **F-003 read-only inventory** now discovers projects, Git repositories/refs, TFVC roots, Graph users/groups and direct memberships, plus Git ACL allow/deny evidence from Azure DevOps Services. Effective repository permissions, verified GitHub organization membership, full TFVC history and real migrations are **not yet implemented**. The platform must never claim a migration completed without verification.

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

Phase 1: local platform identities, credential connections (F-002), Azure DevOps Services inventory (F-003), review-only identity mapping drafts (F-004 MVP), and read-only repository plan preview (F-005 slice 1) implemented; verified effective access grants, code migration and reports are not yet implemented. Future: pipelines → Actions, boards → GitHub Issues/Projects, guest users, AI-assisted transformations. Until explicitly implemented these features are **planned**, not functional.

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

## F-005 repository migration preview

Open **Migration plan / Piano di migrazione** after discovery. Select source repositories, rename GitHub targets and choose a history strategy. The admin-only preview reports invalid names, conflicting target names, incompatible TFVC strategies and explicit missing permission/membership/preflight evidence. **Draft selections can be saved and reloaded in PostgreSQL with optimistic revision checks (HTTP 409 on concurrent edits), but saving is not an approval or migration.** The separate read-only **Check GitHub targets** action detects repositories visible to the saved destination credential (GET 200), and treats GitHub 404 as ambiguous—not proof of an available name. The separate **Check GitHub organization access** action verifies the authenticated destination account's active membership and reads available org private-repo policy. Neither check proves token write/create privilege; `readyForApproval` is still always false. See [F-005 preview documentation](docs/migration-plan-preview.md).

## F-008 durable asynchronous jobs

The **Discovery** workbench can enqueue an asynchronous staging synchronization. Jobs are stored in PostgreSQL, claimed by an atomic `SKIP LOCKED` lease across workers, retried with bounded backoff and recorded in an audit ledger. The same queue can now run a read-only Azure DevOps discovery, checkpoint the immutable source inventory and normalize it to staging, with one active scan per source connection. Status survives restarts and is polled in the UI. Only the read-only `StageSnapshot` and `DiscoverSource` job kinds are supported; no GitHub writes or automatic plan approvals. See [durable job architecture](docs/durable-jobs.md), [asynchronous discovery](docs/async-discovery.md) and [granular source, repository and security-evidence checkpoints](docs/durable-discovery-pages.md). The original direct staging sync remains available during transition.

## F-015 provisional Graph identity observations

Queued Azure DevOps discovery jobs now persist **sanitized Graph user observations per page** in the same lease-fenced transaction as source checkpoints. Identical replay is idempotent; inconsistent identity evidence causes a rollback and a snapshot-consistency check before canonical staging. Admin-only endpoints show provisional page/user counts and expose up to 100 identity records per response **only after job success**. The F-004 identity mapping still reads the complete inline snapshot to avoid false missing users; identity-source memory optimization is a follow-on, not claimed here. See [Graph identity observations and governance boundary](docs/graph-identity-observations.md).

## F-014 source Git ref externalization

The leased Azure DevOps source worker no longer collects all repository Git refs into one .NET list. Each ref page is persisted directly into the PostgreSQL checkpoint/observation tables, and the immutable source inventory contains an explicit `refsExternalized` marker and true `refCount`. The prepared manifest and canonical staging reconcile those persisted rows. Legacy synchronous inventory remains compatible; manual staging of externalized refs fails closed rather than treating the empty inline array as zero source refs. This removes the **Git ref list** from the source worker's memory, but does not make the complete multi-asset discovery constant-memory. See [source ref externalization and tests](docs/externalized-source-refs.md).

## F-014 chunk-driven canonical staging

Durable Azure DevOps discovery jobs now commit **canonical Git ref staging directly from integrity-checked PostgreSQL chunks**. The staging reader receives a slim snapshot with the ref array removed by PostgreSQL; only 250 refs are loaded into worker memory at a time. Existing revision, idempotency, missing-data and audit semantics remain transactionally consistent across ref and non-ref assets. For new leased discovery jobs the source reader and JSONB snapshot now externalize Git refs into PostgreSQL pages; the other source asset families still accumulate in memory, so this is **not yet end-to-end constant-memory discovery**. See [canonical staging from chunks](docs/canonical-staging-from-ref-chunks.md).

## F-014 immutable ref chunk manifests

Completed Azure DevOps discovery jobs now prepare an immutable **SHA-256-verified manifest of Git ref chunks**, built from PostgreSQL observations using a streamed, source-ID ordered cursor. Operators can inspect the sealed manifest and query individual 250-ref blocks through admin-only APIs. The worker strictly compares legacy inline ref snapshots to observations, while new externalized source snapshots require an exact observation count and validated, lease-fenced source pages. Only successful jobs expose manifests. Remaining non-ref source families are still materialized in memory, so no overall constant-memory or enterprise-scale claim is made. See [manifest design and tests](docs/immutable-ref-manifests.md).

## F-014 per-page source ingestion

During a queued Azure DevOps discovery, each Git ref page now commits **provisional normalized observations** into PostgreSQL in the same transaction as its immutable F-008 page checkpoint. This provides live ref/page counts, idempotent replay, worker-lease fencing and rollback without changing the authoritative F-013 catalog until full discovery finishes. The Discovery UI labels counts as provisional. This is **not yet constant-memory end-to-end streaming**, since the full immutable inventory is still materialized. See [F-014 design, API and tests](docs/streamed-ref-observations.md).

## F-013 canonical intermediate staging

Each new Azure DevOps discovery now writes an immutable source snapshot and synchronizes a **normalized PostgreSQL asset catalog**. Assets receive stable source IDs, SHA-256 fingerprints, revision numbers and last-seen/changed snapshot references. The Discovery workbench displays added, changed and missing counts and allows idempotent retry. The admin API exposes indexed keyset pagination for future transform workers.

Incrementality currently applies to **catalog revisions between complete source scans**, not to provider-side delta polling, background sync or GitHub writes. Missing objects remain unverified unless source coverage is complete. See [staging architecture and tests](docs/staging-sync.md).

## F-009 / F-010 workload previews

**Pipelines → Actions** reads Azure Pipelines metadata, extracts task types (not scripts or secrets), proposes a disabled GitHub Actions workflow at `.github/workflows/...`, shows missing secrets/variables and project-level service connection candidates, and optionally requests an Azure AI advisory review. **Boards → Issues** previews work items by original Azure ID, displays related commits/PRs/issues, and can verify the existence of mapped GitHub objects. Both are admin-only and **read-only**. Neither creates a workflow, secrets, issues or reconstructed links yet. See [migration workbench documentation](docs/workload-previews.md).
