# F-072 — GitHub App installation identity fence before scoped token mint

## Existing safeguards and the missing proof

F-043 already builds a short-lived, **one-repository** GitHub App
installation token with precisely `contents:write` (and implicit
`metadata:read`), validates the minted token's selection and permissions,
then reads the target repository via the new token. F-051 reuses that
adapter for GET-only remote ref/policy preflight and retains hard
write-disabled fences.

Before F-072, the **configured numeric installation ID** was accepted
as an assumption about the owning organization. A valid token for a
different account or a reassigned configuration was not separately
checked against GitHub's installation ownership metadata.

## F-072 sequence

The existing `GitHubAppScopedWriteCredentialInspector` now performs
the following **before** issuing the installation token:

1. Validate the App ID, installation ID, expected organization and
   target repository names; sign the existing eight-minute App JWT.
2. Call the fixed GitHub endpoint
   **`GET https://api.github.com/app/installations/{installationId}`**
   with that **App JWT**, not with an installation token.
3. Read a **32 KiB bounded** JSON response with the existing duplicate
   property rejection, including nested objects, and require:
   - `id` equals the configured installation ID;
   - `app_id` equals the configured App ID;
   - `target_id` equals a positive `account.id`;
   - both `target_type` and `account.type` are `Organization`;
   - `account.login` matches the operator-bound destination organization;
   - `repository_selection` is `selected` or `all` (both may be
     further narrowed at token issuance);
   - `suspended_at` exists and is **null**;
   - installed `permissions.contents` is `write`.
4. For **any** mismatch, missing claim, contradictory duplicate JSON
   field, malformed/non-numeric identity, body limit, HTTP failure or
   suspended installation, stop **before** calling the token issuance
   endpoint. Return only a safe error code, never body contents.
5. Only on successful attestation continue with the **unchanged**
   `POST /app/installations/{id}/access_tokens` request scoped to the
   single expected repository and `contents:write`. The original
   strict token response validation, fixed `GET /repos/{org}/{repo}`,
   repository-ID cross-check, archived/disabled checks and F-051
   read-only ref/policy operations remain in effect.

Both HTTP calls use fixed `api.github.com` URLs; no caller-provided
URL or redirect is accepted (the existing registered HttpClient rejects
redirects). The installation response is not returned to the API,
persisted in PostgreSQL or exposed in logs. The ephemeral token
remains local to the inspector and is not added to any domain DTO.

**Important nuance:** a valid App installation and reported contents
permission is not proof a Git push would succeed. Branch protection,
rulesets, authorization revocation or target drift can still block it.
F-051/F-050 execution barriers remain explicitly **write-disabled**.
No actual remote write or migration completion is authorized by F-072.

## Regression tests

The existing F-043/F-051 mocked GitHub API tests now verify the order:

`GET installation (App JWT) → POST one-repository token → GET repository
(installation token) → optional fixed-host ref/policy GETs`.

A separate table-driven negative contract validates **13** hostile
installation responses, including wrong installation/App IDs,
organization login/type, target account ID/type, suspension,
insufficient Contents permission, unrecognized selection,
duplicate nested/root claims, malformed JSON, and HTTP 404. Every
case asserts **exactly one GET** and **zero token mint requests**.
Bogus signing keys continue to result in no HTTP traffic.

No database schema migration, background task, GitHub remote write
adapter or API contract change is needed. F-072 stacks on F-071 PR #72;
`main` is unchanged. Validate .NET 10/xUnit, PostgreSQL 17/real-Git,
React/Playwright and Terraform CI gates before review.

For API behavior: GitHub REST *Get an installation for the authenticated
app* requires a GitHub App JWT and reads `/app/installations/{id}`:
https://docs.github.com/en/rest/apps/apps#get-an-installation-for-the-authenticated-app
