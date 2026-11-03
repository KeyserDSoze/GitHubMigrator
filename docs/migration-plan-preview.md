# F-005 — Repository migration plan preview (slice 1)

Status: **read-only preview and versioned PostgreSQL plan drafts implemented**. Target availability preflight, approval and execution are **not implemented**.

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

## Validation semantics

- **Blockers** include invalid target names, case-insensitive duplicate target names, TFVC selected with GitMirror, no repositories selected, incomplete source identities and unverified effective permissions.
- **Always-unverified blockers in this slice:** destination repository-name availability, repository-creation privileges and organization membership. The initial F-002 GitHub validation only proves the token can authenticate and inspect the organization.
- **Warnings** include a missing identity mapping draft and Git strategies that could reduce retained history.
- Candidate mapped logins, where available, are **not GitHub access grants**. The current discovery ACL masks are not an effective-permission calculation; missing or inherited access must not be silently treated as an empty or verified permission set.
- Repository existence and permission grants are not checked against live GitHub here, so `TargetRepositoryAvailabilityVerified` and `TargetMembershipVerified` are always false.

## Next F-005 slices

Implemented persisted and versioned drafts with compare-and-swap. Next implement safe GitHub GET-only preflight (distinguishing 404 from unauthorized/hidden repositories), membership and create-permission checks using least privilege, then an explicit audited approval step that freezes the exact snapshot and plan version. F-008 will consume only approved plans for durable jobs; F-006/F-007 will perform actual migration and verification.

No status should be represented as approved or migrated until those gates exist.
