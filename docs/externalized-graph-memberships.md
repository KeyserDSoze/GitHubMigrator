# F-021 — PostgreSQL-backed direct Graph memberships

This increment builds on F-020. **Only Azure DevOps Graph direct (depth=1)
membership observations** are handled. Do not infer nested membership,
Azure DevOps effective permissions, GitHub permissions, invitations, or
migration approvals from these records.

## Durable discovery

The leased GET-only security reader persists allowlisted membership page
checkpoints under `security-members:<first-40-hex(SHA256(group descriptor))>`.
Each successful checkpoint transaction adds normalized records to
`discovery_membership_observations` in batches of 250, with a natural
primary key `(job_id, source_id)`, a group index, canonical SHA-256 and
a foreign key to the exact source page. Duplicate provider edges are
idempotently deduplicated. Unrelated Graph containers are ignored just as
in the synchronous source reader; invalid canonical keys and fingerprint
disagreements are rejected. A lease expiry or conflicting observation
rolls back the *entire* checkpoint and its audit event. A retried page can
be backfilled from PostgreSQL without another provider GET.

For a leased reader with an observation-capable store, the immutable
`DiscoveryInventory` has `memberships: []`,
`membershipsExternalized: true` and a distinct
`membershipCount`, up to the existing 20,000-edge guard. The array's
emptiness does **not** mean there are no source memberships. Legacy and
synchronous readers retain inline memberships and the old snapshot model.

## Verification and canonical staging

The worker verifies its live, unique discovery lease and snapshot binding;
it compares the full direct-edge set against saved allowlisted page
checkpoints with symmetric `EXCEPT ALL`. Complete source membership
coverage requires every enumerated group to have contiguous pages from
zero and a terminal page, including groups with no members. Equal-count
tampering, missing pages, unexpected group keys and externalized count
mismatches block staging. Replays of pre-F-021 pages are backfilled under
the current lease.

The source-scoped staging transaction reads membership observations by
indexed source-ID keyset in batches of 250; every row is renormalized and
must match its canonical source ID, group descriptor and fingerprint
before UPSERT. Source transaction rollback protects all asset types if
a member fails integrity checks. The F-013 count and missing-detection
semantics remain: incomplete membership coverage never marks unseen
edges missing. Manual staging of externalized snapshots is rejected.
A successful job must still seal a valid Git ref manifest.

## Protected API

Admin and superadmin with `PasswordChanged` policy:

- `GET /api/jobs/{id}/membership-progress`: provisional page and distinct
  membership counts (available during an in-progress discovery).
- `GET /api/jobs/{id}/memberships?take=50&after=...`: keyset-paged
  allowlisted raw edges, at most 100 per response, **only after success**.
  Invalid page arguments return 400; pending jobs return 409.

Discovery summaries and the React workbench report the true
`membershipCount` and explicitly mark data as PostgreSQL-backed.
No permission assertion is made.

## Validation and limitations

Integration tests cover 521 edges including duplicates, multi-page
source ingest, an empty group, canonical SHA parity, pagination,
replay without duplicate audit events, fail-closed same-count
tampering, invalid paging, worker fencing, and 250-row staging.

The remaining project/repository and Graph group data still use inline
snapshots. Raw ACL evidence is not externalized. There are no
benchmarked production throughput promises, reviewed EF migrations,
continuous delta polling, approved destination GitHub writes, or
effective-permission calculations yet.
