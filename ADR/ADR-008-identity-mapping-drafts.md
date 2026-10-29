# ADR-008 — Review-only identity mapping with optimistic concurrency

Status: Accepted for F-004 MVP

## Context

A source user and a target GitHub login are identifiers in different security domains. Azure DevOps source ACLs and GitHub organization memberships have not been verified as effective per-user access. Concurrent platform administrators may edit the same migration plan and overwrite assignments.

## Decision

- Store a `IdentityMappingDraft` scoped by immutable discovery snapshot ID plus **verified GitHub destination connection ID**, not a global mutable list.
- Accept only source identity IDs actually present in the snapshot; validate GitHub login syntax; reject case-insensitive collisions; no email/display-name guesses or automatic proposals.
- Keep proposed mapping in JSONB, not connection secrets. Each draft has an integer revision.
- PostgreSQL updates must be atomic: insert-only at expected revision 0, conditional UPDATE for later expected revisions, with 409 conflict on lost update.
- Preview missing identity mapping, source/target permission verification gaps, and unresolved ACE principals separately. Zero known unmapped identities does not imply organization grant readiness.
- Limit API to password-changed platform administrators; never write to GitHub from this component.

## Consequences

- Users can review and incrementally edit mapping drafts safely without pretending to have completed an access migration.
- Subsequent snapshots or destination organizations require independent drafts; no stale automatic carryover.
- Usernames remain unverified until a separate GitHub organization membership preflight.
- Effective permission calculation, approval workflows, audit retention, and database migration hardening remain mandatory before production.
