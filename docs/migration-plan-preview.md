# F-005 — Repository migration plan preview (slice 1)

Status: **read-only preview, versioned drafts, GitHub target existence and authenticated organization membership/policy review implemented**. Token create permissions, full availability, approval and execution are **not implemented**.

## Operator workflow

1. Run Azure DevOps Services discovery (F-003), retaining the immutable snapshot.
2. Create and verify a GitHub Enterprise Cloud destination connection (F-002).
3. Optionally save an identity-mapping draft (F-004). A mapped login alone is not proof of GitHub membership or permission.
4. Open **Migration plan / Piano di migrazione**. Pick a snapshot and destination, select repositories explicitly, set the desired GitHub repository names and history strategies.
5. Run **Validate plan preview / Verifica anteprima piano** to inspect repository mappings, inventoried ref counts and warnings/blockers.

The UI never performs GitHub writes. Operators may now save or reload a draft bound to a snapshot/destination pair. Unsaved changes are lost on refresh; a saved draft persists in PostgreSQL. Saving does not approve a plan, create a repository, assign access or enqueue a migration.

## API

`POST /api/migration-plans/preview` accepts:

```json
{
  "snapshotId": "11111111-1111-1111-1111-111111111111",
  "destinationConnectionId": "22222222-2222-2222-2222-222222222222",
  "repositories": [
    { "sourceRepositoryId": "azure-repo-guid", "targetRepositoryName": "new-name", "strategy": 0, "include": true }
  ]
}
```

`strategy` is the numeric `HistoryStrategy` enum: GitMirror=0, TipOnly=1, FullHistoryBestEffort=2, ArchiveAndTip=3. `include=false` excludes a repository. The request is limited to 1,000 unique known source repository identifiers. The endpoint requires an authenticated `admin` or `superadmin` who has completed the first-login password change.

Response: snapshot/destination binding, target organization, identity-draft revision, selected and total repository counts, proposed source→target pairs, inventoried ref counts, candidate mapped identities only where effective source permission evidence is verified, plus severity-coded validation checks. `readyForApproval=false` is intentional until the missing provider checks exist.

## Versioned draft API

- `GET /api/migration-plans/drafts/{snapshotId}?destinationConnectionId={guid}` returns an existing saved draft, or revision 0 with an empty list for an uninitialized pair.
- `PUT /api/migration-plans/drafts/{snapshotId}` accepts `destinationConnectionId`, `expectedRevision` and `repositories` with the same selection format as preview.
- Revisions start at 1 after the first save. `expectedRevision=0` is insert-only; subsequent saves atomically compare the expected revision with the stored revision. If another admin or browser tab saved first, the API responds `409 PlanRevisionConflict`. The UI preserves local edits and offers an explicit reload.
- Each snapshot/destination combination has an independent draft, even if the source org or target org names match. A verified GitHub destination and existing snapshot are required.
- Drafts can contain unresolved blockers; operators may save work in progress, but no endpoint approves or executes it.
- The PostgreSQL schema is provisioned by the current additive bootstrap DDL for compatibility with existing dev databases; this must be replaced by reviewed EF migrations before production.

## GitHub target existence preflight (GET only)

The **Check GitHub targets / Controlla target GitHub** button invokes `POST /api/migration-plans/preflight` with the same snapshot/destination/repository selection contract as preview. The API accepts 1–10 selected repositories with unique syntactically valid target names, uses the saved verified GitHub destination credential, and sends **only GET** requests to the fixed GitHub API host `api.github.com/repos/{organization}/{repo}`. Redirects and cookies are disabled; requests are rate-limited and have a deadline. No user-supplied hostname is accepted.

| Probe result | Meaning |
| --- | --- |
| `Exists` | HTTP 200 JSON has the exact expected `full_name`: a visible destination repository already occupies the name |
| `NotVisibleOrMissing` | HTTP 404: the repository is absent **or hidden from the token**; this is **not** proof the name is free |
| `Unauthorized`, `Forbidden` | Lookup cannot establish visibility; access requires review |
| `RateLimited`, `Unavailable`, `InvalidResponse` | Provider unavailable, throttled, or response cannot be trusted; retry or investigate |

The response includes the lookup timestamp and statuses, but deliberately keeps `targetAvailabilityVerified=false` and `readyForApproval=false`. Reading repository metadata does **not** verify GitHub organization membership, repository creation rights or the ability to migrate content. The `404` ambiguity can only be resolved with additional safe provider authorization checks or an explicitly approved creation operation, and must not be promoted to an “available” success state.

## GitHub organization access review (GET only)

The **Check GitHub organization access / Verifica accesso organizzazione GitHub** action calls `GET /api/migration-plans/destinations/{destinationConnectionId}/access`. This is admin-only and independently rate-limited. It accepts **only a saved, verified GitHub Enterprise Cloud destination connection**. The authenticated login is retrieved from the saved F-002 validation record, **not from a user-supplied login field**; the credential comes from the existing secret store.

Two fixed-host, read-only GitHub calls may be made:

1. `GET https://api.github.com/user/memberships/orgs/{org}` — the response must match both the saved GitHub login and destination organization, with `state=active`. Pending memberships, mismatched identities, 401/403/404, throttling and unavailable responses never establish active membership. A 404 is reported as **not confirmed**, rather than asserting a non-member.
2. When membership is confirmed active, `GET https://api.github.com/orgs/{org}` reads the optional `members_can_create_private_repositories` setting (if returned) as **organization policy only**. It does not prove that the saved token can create repositories. Member/owner role does not prove credential write grants either.

The returned evidence exposes `membershipVerified` only for a validated active self-membership, but keeps `repositoryCreatePermissionVerified=false` and `readyForApproval=false` for **all** results. GitHub's create-repository endpoint needs write-capable scopes and the relevant organization policy; exercising it would be a write, and is intentionally out of scope here. No GitHub user/password/token values are logged or returned. The adapter bounds response bodies, avoids redirects/cookies and never accepts arbitrary hosts.

See [GitHub membership REST API](https://docs.github.com/en/rest/orgs/members) and [Create an organization repository](https://docs.github.com/en/rest/repos/repos#create-an-organization-repository).

## Validation semantics

- **Blockers** include invalid target names, case-insensitive duplicate target names, TFVC selected with GitMirror, no repositories selected, incomplete source identities and unverified effective permissions.
- **Unverified approval blockers:** name availability is still ambiguous for hidden 404 repositories; repository creation permission and token write scopes remain unverified. Organization membership may now be individually confirmed for the saved authenticated account via a separate read-only probe, but not for arbitrary identity mappings.
- **Warnings** include a missing identity mapping draft and Git strategies that could reduce retained history.
- Candidate mapped logins, where available, are **not GitHub access grants**. The current discovery ACL masks are not an effective-permission calculation; missing or inherited access must not be silently treated as an empty or verified permission set.
- The standalone preflight can confirm visible target collisions and the new destination-access route can confirm membership of the saved authenticated account. The **plan preview's** `TargetRepositoryAvailabilityVerified` and `TargetMembershipVerified` fields remain false because the evidence is not yet bound to an immutable approved plan or to all mapped GitHub principals.

## Next F-005 slices

Implemented persisted and versioned drafts with compare-and-swap. GET-only GitHub target existence probes now distinguish 200 collisions from ambiguous 404. Authenticated destination organization membership and creation *policy* checks are implemented, but token write/create privileges and mapped-user membership remain unverified. Next add approval readiness evidence binding, least-privilege authorization design, then an explicit audited approval step that freezes the exact snapshot and plan version. F-008 will consume only approved plans for durable jobs; F-006/F-007 will perform actual migration and verification.

No status should be represented as approved or migrated until those gates exist.
