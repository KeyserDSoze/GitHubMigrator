# F-004 — Identity mapping drafts (review-only MVP)

**Status:** versioned draft API, PostgreSQL persistence, IT/EN React UI and deterministic backend/frontend contract tests implemented. No Azure DevOps live-PAT or Docker-backed PostgreSQL smoke tests have been run from this environment.

## Workflow

1. In **Connections**, verify a GitHub Enterprise Cloud destination. This validates the token identity and organization existence; **it does not establish write permission or membership of mapped users**.
2. In **Discovery**, obtain an immutable source snapshot that contains Azure DevOps Graph users.
3. Open **Identity mapping / Mapping utenti**; choose the source snapshot and GitHub destination connection.
4. For each discovered source user, optionally enter the intended GitHub **login**. Use known identity evidence; never assume equal emails/display names imply the same account.
5. **Save mapping draft**. The API records only source descriptor, target login, snapshot/destination IDs, revision and timestamp.
6. Inspect mapped/unmapped counts, unresolved ACL descriptors and explicit issues. Missing scope or effective rights verification block grant-ready status.

No GitHub or Azure DevOps write endpoints are called by F-004. Mapping is not a grant or approval. No credential reference, PAT or token is present in draft JSON.

## API

Routes require the `PasswordChanged` policy and `admin` or `superadmin` role.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| GET | `/api/identity-mappings/{snapshotId}?destinationConnectionId={uuid}` | Return draft + review; revision zero if none exists |
| PUT | `/api/identity-mappings/{snapshotId}` | Atomically save draft and return updated review |

Example payload:

```json
{
  "destinationConnectionId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
  "expectedRevision": 0,
  "mappings": [
    {
      "sourceIdentityId": "aad.ABCD",
      "gitHubLogin": "example-gh-user"
    }
  ]
}
```

Every PUT replaces the full draft mapping list. Empty entries are omitted by the UI and an empty array intentionally clears a draft. `expectedRevision=0` means create-only. Later revisions are updated conditionally within PostgreSQL; a stale revision returns `409 RevisionConflict` without overwrite. The API rejects missing snapshots/destinations (404), unsupported/unverified destinations and invalid mappings (400). Only provider IDs already listed in the selected snapshot can be mapped. GitHub login format is checked and multiple source IDs cannot silently reuse the same login.

## Review completeness and security

A preview includes `discoveredIdentities`, `mappedIdentities`, `unmappedIdentities`, `unresolvedAclPrincipals`, and issue codes such as:

- `SourceIdentitiesIncomplete`: Graph scan scope or page coverage missing.
- `UnmappedSourceIdentity`: known source descriptor has no mapping, **not** a proven orphaned grant.
- `AclPrincipalUnresolved`: legacy ACL descriptor not joinable to a Graph user/group `legacyDescriptor`.
- `EffectivePermissionsUnverified`: nested/external groups, inherited ACEs, branch ACLs, deny precedence not validated.
- `TargetMembershipUnverified`: proposed GitHub login not verified as a member/guest of the target org.

`ReadyForGrants=false` and `TargetAccountsVerified=false` by design. Even when all users are mapped, the system must not grant GitHub access or claim source ACL fidelity.

**Privacy and governance:** identity descriptors, display names and source emails are admin-only. Drafts contain target account names and source IDs, not credentials. Review responses show only the first 100 missing mappings and 25 unresolved ACL warnings, with full numeric counts; UI lists at most 250 matching users per search and does not silently discard the rest when saving (all staged mappings remain in client state).

**Limitations:** the current API does not prove mapped GitHub users exist or belong to the organization and cannot currently approve or execute access migrations. The versioned PostgreSQL schema uses the existing phase-0 additive DDL bootstrap instead of reviewed EF migrations; concurrency is implemented with PostgreSQL conditional SQL but has not yet been exercised against a live database. Identity draft retention/audit history, large-org paging, and organization membership checks remain future work.

## Next

F-004 follow-up: GitHub account and target organization membership validation (read-only, consent/visibility-aware), source effective access evaluation with traceable evidence, review/approval gates. F-005 then plans repository naming/selection without auto-creating grants.
