# GitHubMigrator

An extensible, feature-driven platform to migrate source control and (later) pipelines and work tracking from Azure DevOps Services, Azure DevOps Server / TFS, and other systems to GitHub Enterprise.

**Engineering status (through F-092):** Azure DevOps Services read-only discovery, PostgreSQL staging and durable source sync, Git ref/identity/ACL evidence, versioned source-only checkpoints, per-ref SHA-256 attestation, and read-only paged ref-change inspection are implemented with automated tests. Local Git sandbox transfer and tightly guarded remote-send/recovery foundations exist but are **not an approved, unattended, production GitHub migration flow**. Effective grants, complete TFVC conversion, automatic destination sync and end-to-end verified migration remain open. Never claim a migration completed without independent verification.

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

## Verified source sync checkpoints and GitHub review (F-090–F-093)

The Source sync workbench captures explicitly reviewed immutable source snapshots. New F-091 receipts preserve the original F-090 digest while binding a versioned SHA-256 of every changed source Git ref in the F-025 journal. **F-092** adds a superadmin-only, read-only API and React preview that page through recorded branch/tag changes using an exact PostgreSQL keyset cursor, with their previous/current fingerprints and reviewed target mapping. Historical count-only F-090 receipts remain readable, but cannot impersonate F-091 attested per-ref history. No feature in this section dispatches a GitHub push.

**F-093** adds a superadmin-only, GET-only cross-observation review: select a current, previously verified F-031 GitHub target observation from the Source sync panel and compare its recorded ref names with an F-091 source checkpoint. The comparison distinguishes names present/absent at observation time, while source changes remain classified independently. No equality of Git object IDs, ancestry, safe push or actual migration completion is inferred.

See [F-091 fingerprint attestation](docs/github-source-ref-fingerprint-manifest-f091.md), [F-092 bounded ref delta pages](docs/github-checkpoint-ref-delta-pages-f092.md), and [F-093 source/GitHub name-only review](docs/f093-checkpoint-github-target-name-review.md). Full SHA verification is rerun on each page, so end-to-end page cost still scales with checkpoint and target observation sizes until a persisted verified cursor is added.

## F-094 offline Git object ancestry foundation

A separate, **disabled-by-default local-only audit** now checks physical Git commit ancestry between two self-contained bare mirrors. The bounded audit is SHA-1/SHA-256 aware and can classify identical ref tips, possible fast-forward, target-ahead, divergent history, missing objects, target-only refs and changed tags. It seals the decisions to the source checkpoint and destination observation proof identities and performs strict fsck/closed-object checks before and after graph inspection.

This is **not yet wired to an externally actionable source-sync endpoint or GitHub remote push**. Offline fast-forward candidates stay blocked until the independently verified GitHub destination, live object closure, policy, authorization and replay fences can be enforced. See [F-094 design, limits and real Git tests](docs/f094-local-incremental-git-ancestry-audit.md).

## F-095 durable local-only ancestry evidence

The internal F-095 ledger binds an offline F-094 Git object ancestry receipt
to a verified F-091 source checkpoint, an F-032/F-014 original source ref/OID
proof and a previously sealed F-031 GitHub observation. PostgreSQL records
one immutable receipt plus its append-only event atomically, rejects stale
observations and forbids write/retry/dispatch/migration-success flags.
**F-096** now requires an independently verified, workpack-specific F-060/F-061
local bare mirror for new durable ancestry audits: the original F-060
SHA-256, ref/OID set, lifecycle event and local object closure must match.
Legacy F-094/F-095 receipts retain their original content hashes.
The ledger and physical auditor remain **off by default**. Only F-029
baseline snapshots with sealed F-014 ref manifests are supported;
there is still no automatic GitHub destination update. See
[F-095/F-096 ancestry ledger](docs/f095-durable-offline-git-ancestry-ledger.md).

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

## Simulated source and target for end-to-end tests

`docker compose up` also starts **ado-sim** (an Azure DevOps Server-compatible source with a deterministic estate, [docs](docs/ado-test-environment.md)) and **gh-sim** (a GitHub Enterprise Server-shaped target with real git, GitHub App tokens and rulesets, [docs](docs/github-test-environment.md)). and **llm-sim** (an OpenAI-compatible model stand-in with scripted, hostile and faulty answers for the AI conversion gates, [docs](docs/llm-test-environment.md)). `./script/dev.sh lab` points the API at gh-sim and allows llm-sim for AI connections; by default the API still talks to github.com. Contract tests of the simulators run without PostgreSQL. See [ADR-009](ADR/ADR-009-github-endpoints-and-simulated-test-bed.md) and [ADR-010](ADR/ADR-010-git-remote-policy-and-services-stand-in.md). `./script/dev.sh lab-services` makes ado-sim imitate Azure DevOps Services. The simulators are not an oracle: [live smoke tests](docs/live-smoke-tests.md) check them read-only against real Azure DevOps Services and GitHub.

AI-assisted pipeline conversion returns a *proposal* that is kept and approved by a person against its hash, with a read-only plan for the pull request, an opt-in, off-by-default publisher that has only met the GitHub simulator, and the original Azure triggers brought back only as a separate, deterministic proposal that is approved on its own and can replace the file the conversion put there, with a read-only check of each attempt against the destination (model connections, redaction, output gates, task accounting): see [docs/ai-conversion-gates.md](docs/ai-conversion-gates.md).

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

## F-019 persisted Graph security-group pages

Queued source discovery now saves allowlisted Azure DevOps Graph security-group observations transactionally with each immutable page checkpoint. The worker rejects duplicate or conflicting evidence and verifies groups against the source snapshot before F-013 staging; admin-only post-success pages and provisional Discovery counters support inspection. **These are raw security observations, not effective permission grants.** The groups/memberships/ACL arrays are still inline in the source snapshot. See [F-019 design](docs/graph-security-group-observations.md).

## F-018 Graph identity source externalization

A leased Azure DevOps discovery job now **avoids accumulating Graph user records in .NET**. User metadata is checkpointed transactionally in PostgreSQL, and the immutable source snapshot stores a true `identityCount` plus `identitiesExternalized: true` instead of inline users. F-004/F-005 consult the snapshot-bound verified observations; the durable staging worker imports identities in 250-row batches with hash, count and lease checks, preserving partial-source and rollback semantics. Legacy synchronous snapshots remain compatible. **Projects, groups, memberships and ACLs still use in-memory discovery**. See [F-018 design, tests and limits](docs/externalized-graph-identities.md).

## F-017 snapshot-bound F-005 plan evidence

F-005 now reads source identity metadata and Git ref counts from **the selected immutable PostgreSQL snapshot**, rather than interpreting empty inline ref arrays in durable discovery as zero source refs. New source jobs require completed sealed manifests and a bidirectional source-page/observation identity-and-OID check; historical inline snapshots use bounded SQL JSONB aggregation. Every preview remains **read-only and not approved**. See [plan snapshot evidence](docs/plan-snapshot-evidence.md).

## F-016 paged F-004 identity mapping

The F-004 reviewer now validates, counts and pages source identities directly in PostgreSQL against the chosen **immutable snapshot** instead of reading all identities into .NET. Successful source jobs use reconciled F-015 Graph observations; legacy snapshots use bounded JSONB queries. The mapping editor loads 100 users per page and preserves edits from other pages when saving the full revisioned draft. Raw ACL evidence remains unresolved and **no GitHub grants/approvals are enabled**. F-005 and upstream source collection still use inline identities. See [F-016 design and tests](docs/paged-identity-mapping.md).

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
