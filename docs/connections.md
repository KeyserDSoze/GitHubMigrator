# F-002 — Connection management

## Scope and current status

**Implemented:** Azure DevOps Services source connections and GitHub Enterprise Cloud destination connections with PATs, read-only validation, persist/list/verify/rotate/delete, admin-only API and UI (English/Italian, light/dark).

**Not implemented:** Azure DevOps Server/TFS Server, GitHub Enterprise Server, GitHub Apps/Entra authentication, organization *write permission* preflight, connection ownership, background refresh, full project/user discovery or actual Git writes.

## API (platform admin or superadmin + password already changed)

| Method | Path | Payload | Response |
| --- | --- | --- | --- |
| GET | `/api/connections` | – | Safe metadata list |
| POST | `/api/connections/validate` | `{ name, providerKey, organization, personalAccessToken }` | `{ isValid, code, authenticatedAs, permissionAssessment }` (PAT not persisted) |
| POST | `/api/connections` | Same | 201 metadata; 422 for rejected remote credentials |
| POST | `/api/connections/{id}/verify` | – | Updated metadata (retrieves stored PAT privately) |
| PUT | `/api/connections/{id}/credential` | `{ personalAccessToken }` | Updated metadata (no replacement on failed validation) |
| DELETE | `/api/connections/{id}` | – | 204 and credential deletion |
| GET | `/api/providers` | – | Available vs planned providers |

Connection metadata includes provider key, direction, organization, verification code and timestamp. **No API returns the PAT or internal secret reference**. The caller's JWT must be valid and have `password_changed=true`.

## Validation guarantees

- `azure-devops-services`: GET `https://dev.azure.com/{organization}/_apis/projects?$top=1&api-version=7.1` with PAT using Basic authentication. `Verified` means the PAT can read the project listing; it says nothing about downstream TFVC code, identities or permission APIs.
- `github-enterprise-cloud`: GET `https://api.github.com/user` with bearer PAT, then GET `https://api.github.com/orgs/{org}`. `Verified` means a valid authenticated identity and an existing visible organization. **An organization may be publicly visible without token membership or repository creation permission**. UI therefore displays `organization_write_unverified` until a later preflight.
- Organization input is an *identifier*, not a URL; the API constructs fixed HTTPS hosts and disables HTTP redirect following and cookies. Supports no arbitrary custom domains yet to reduce SSRF and PAT exfiltration risk.
- On failures return stable safe codes: `Unauthorized`, `InsufficientScope`, `OrganizationNotFound`, `ProviderTimeout`, `ProviderUnavailable`, `UnexpectedResponse`, `UnexpectedRedirect`. Do not relay provider bodies, headers or raw exceptions.
- A timeout is 15 seconds. Authenticated endpoints have an additional fixed-window rate limiter to limit unnecessary outbound requests.

## Secret storage

- Local Development: AES-256-GCM encrypted ciphertext, unique random nonce per write and associated data bound to connection reference; encryption key is an ignored local environment variable.
- Azure: Key Vault SecretClient with DefaultAzureCredential and managed identity, secret named `connection-{guid}`. PostgreSQL holds only the logical reference.
- Metadata deletion attempts secret deletion before removing the metadata row. A cross-system transaction is not available; reconciliation of orphaned secrets after partial failure is a future hardening item.
- Current API uses short-lived JWTs, no persistent browser JWT storage, and platform RBAC separate from migrated source users.
- PATs are still user-managed credentials. Encourage least-privilege scopes and short expiry, and rotate them regularly. For a production SaaS migrate toward Entra/GitHub App integrations.

## Acceptance and test strategy

- Business unit tests use fake validator, repository and secret store; confirm no secret in metadata and no persistence for invalid credentials.
- HTTP validator unit tests use fake HTTP handlers and verify provider host, auth header, safe statuses.
- Playwright mocks API and checks connection workflows without real PATs.
- Actual Azure DevOps/GitHub integration and Docker-backed PostgreSQL/Key Vault contract tests require credentials and running infrastructure; they are *not covered* by the default CI.
