# ADR-005: Provider-neutral connections and protected credentials

Status: Accepted for initial F-002; revisit before production

Context: User-provided PATs need to be verified and reused for discovery without leaking into snapshots, Git history, logs, UI responses or database plaintext.

Decision:
- Separate connection metadata from credentials through `IConnectionRepository` and `IConnectionSecretStore`; only admins manage connections.
- Use adapter-specific read-only probes. Azure DevOps Services and GitHub Enterprise Cloud are enabled; server/TFS custom host URLs are deferred because their network destinations require SSRF protection and support-specific auth.
- Use AES-256-GCM encrypted PostgreSQL credential rows locally with a .env.local encryption key; in Azure use Key Vault via managed identity with Secrets Officer role. Never return PATs or secret references.
- A GitHub `/user` plus public `/orgs/{org}` probe does not prove write permission. Migration can only start once a separate write-permission preflight is introduced.
- Do not log raw provider response bodies, user PATs or HTTP authorization headers.

Consequences:
- Provider metadata remains portable, and credentials can rotate independently.
- The local encryption key must be protected and backed up separately; an example/test key is *not a production key*.
- Key Vault secret write permissions are required; the initial environment grants Secrets Officer. Consider a narrower/custom role or a dedicated credential service for production.
- HTTP redirection disabled; input is currently organization IDs only. Multiple server/collection URL support needs a new decision and network isolation.
- Azure secret deletion and SQL deletion aren't transactional; compensating cleanup/reconciliation is planned.
- Schema remains phase-0 additive PostgreSQL DDL/EnsureCreated until EF migrations are introduced. Do not treat this as production-ready.
