# F-097b — Immutable PostgreSQL proof ledger (internal, off by default)

Tracks issue [#94](https://github.com/KeyserDSoze/GitHubMigrator/issues/94) under parent [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

## Schema and trusted writer

F-097b adds `github_checkpoint_source_ref_proofs` and
`github_checkpoint_source_ref_proof_events`, both append-only and
SQL-trigger-guarded, with immutable SHA-256 metadata, foreign keys, a unique
`(checkpoint_id, source_repository_id)` key, a single `Recorded` event,
and hard database constraints prohibiting push, retry, dispatch and migration
success. Startup installs the schema after the existing F-090 tables.
No API route or sender is registered; the internal journal defaults disabled.

The internal `PostgresGitHubSourceCheckpointGitRefLedger` uses the same
source-first F-013 / F-090 PostgreSQL advisory lock and a plan-scoped
second lock. It reads only an independently reverified F-091 checkpoint and
**the exact** completed, sealed, externally stored F-014 Git-ref snapshot.
The new ref reader does not read mutable `staging_assets`.
No client-supplied branch, OID, root SHA or prior proof is accepted.

The F-097a proof seals exact source repository, snapshot and checkpoint,
ref/OID set, F-014 root, F-091 ref attestation, and the previous
**full-ref** proof. Later epochs are refused unless each predecessor
has already been written and can be reverified against its source
manifest and historical F-091 receipt. Recursion is bounded to 64 epochs.
Exact same-actor replay returns the already-verified proof; a second actor
may not silently claim the immutable record. Readback recomputes the
whole chain under PostgreSQL repeatable-read and checks its audit event.

## Boundaries

This is evidence bookkeeping and **not** a runnable GitHub sync engine.
The F-095/F-096 physical mirror bridge still does not accept later F-091
checkpoints. Git objects are not fetched, sent or retried by this ledger,
and an F-097 receipt has **no** migration-authority flag. F-014 full-ref
coverage is independent of Azure DevOps effective ACL verification; an
unknown source discovery error still fails closed. This first workpack
supports 1–128 refs per selected repository, homogeneous SHA-1 or SHA-256.
Real PostgreSQL regression tests validate additive/idempotent schema
bootstrap, disabled mode, fail-closed legacy source admission, write-deny
CHECK constraints, immutability triggers and restart readback of the
SQL ledger and Recorded event. A positive complete-source **trusted
capture** integration fixture is not yet implemented and must be
verified before issue #94 is closed.

Larger pages, complete real F-014→F-090→F-097 integration and
F-095 physical-bound source history remain tracked in #94/#92 until verified.
