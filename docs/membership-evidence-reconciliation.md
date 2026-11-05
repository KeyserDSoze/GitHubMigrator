# F-020 — Immutable Graph membership checkpoint reconciliation

**Scope:** reject altered or incomplete direct Azure DevOps Graph group-membership evidence before the source worker advances to canonical staging. This is a database-side verification increment on top of F-019, **not** externalized membership collection or effective permissions.

## Pipeline

1. The GET-only Graph security adapter already saves sanitized `containerDescriptor` and `memberDescriptor` pages under `security-members:<sha256(group descriptor)[0..40]>` through the F-008 live lease. No provider token or raw response enters those checkpoints.
2. Following the F-015 identity and F-019 group verifiers, the worker requires a valid *running*, unexpired source discovery lease whose snapshot ID equals its job ID.
3. PostgreSQL derives every expected group-scoped resource key from the **immutable** `migration_discoveries.inventory.groups`, then joins its checkpoint pages. Matching uses exact casing of source descriptors, and excludes unrelated/missing-member edges exactly like the source adapter. Duplicate provider edges are deduplicated, as in the source adapter.
4. A bidirectional `EXCEPT ALL` verifies the entire direct-membership set against `inventory.memberships`. Equal edge counts with swapped group/member IDs still fail.
5. Unexpected group page keys or invalid page value arrays fail. When `membershipsComplete` is true, every enumerated group must have contiguous pages from index zero with **exactly one terminal page** at the end, even for groups with zero members. `membershipsComplete` may not be true if group enumeration is incomplete.
6. Failures raise the stable `MembershipSnapshotMismatch` code before staging or worker success. A stale lease raises `DiscoveryLeaseLost`. Partial membership collection can be staged only as **partial raw evidence**, without missing-member deletion inference.

This reuses existing PostgreSQL checkpoint pages and adds **no** large membership array to the verifier or new database table. It does not change F-013 staging, legacy snapshots, API contracts, ACL semantics, or GitHub destination writes.

## Tests

Real PostgreSQL integration cases cover: duplicate provider edges over multiple pages; a group with no members; same-count member swap; missing group/terminal pages; orphan group pages; incomplete-but-consistent collection; expired worker lease. The existing .NET and CI test suites remain the release gate.

## Next increments

Persist indexed membership-edge observations and stream source membership acquisition in bounded pages. Afterwards, externalize snapshot-bound security readers and raw ACL evidence; add enterprise-scale load measurements, schema migrations, recovery/retention and governance checks before enabling approved GitHub writes.
