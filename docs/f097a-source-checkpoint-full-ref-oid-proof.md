# F-097a — Full source Git ref/OID checkpoint proof (internal core)

Tracks implementation issue [#93](https://github.com/KeyserDSoze/GitHubMigrator/issues/93) under full migration feature [#92](https://github.com/KeyserDSoze/GitHubMigrator/issues/92).

The F-091 source-change journal authenticates **fingerprints of changed refs**, not all current Git ref object IDs. In particular, a downstream consumer must NEVER derive an OID from a SHA-256 staging fingerprint, re-read mutable `staging_assets` and assert it is the immutable source snapshot, or reuse F-029's baseline F-014 manifest for a later F-090 snapshot.

## Implemented in this slice

`GitHubSourceCheckpointGitRefProofIntegrity` is an **internal business proof core**, with no database writer, API route or sender. It accepts only an already verified F-091 version-2 checkpoint and a typed `VerifiedSourceGitRefs` for **the exact checkpoint snapshot**, which the future PostgreSQL adapter MUST derive from a separately verified, **complete and sealed** F-014-style snapshot ref/OID manifest. It rejects legacy count-only checkpoints, incomplete source scans, missing manifest roots, noncanonical refs, mixed SHA-1/SHA-256 object IDs, missing/foreign predecessor full-ref proofs and >128-ref local ancestry workpacks.

The version-1 F-097a SHA-256 proof binds:

- F-090/091 checkpoint ID, complete immutable checkpoint SHA and F-091 attestation SHA, source/plan/baseline/snapshot IDs.
- Exact source repository identity, source F-014-style manifest root and v1 verified source evidence SHA.
- For later snapshots, the previous checkpoint ID **and** previous full-ref proof SHA; the predecessor must match the F-090 checkpoint's independently verified previous F-090 digest, source, plan, baseline and repository.
- Complete canonical ordered `refs/heads/*` / `refs/tags/*` names and original Git OIDs, one object format per proof. All flags for push, dispatch, retry and migration completion stay false.

The proof is deterministic and re-verifiable with the **original verified inputs**. This is only a *foundation*: it does not prove that a caller-created `VerifiedSourceGitRefs` came from Azure DevOps or that a later snapshot already has the required F-014 manifest. No production code invokes the new proof until the immutable source-ref persistence and verification in #92 are implemented. No F-095/F-096 ancestry acceptance of later checkpoints is enabled.

## Tests and remaining work

Unit tests cover SHA-1/SHA-256 complete baseline/incremental chains, deterministic canonicalization, exact predecessor matching, legacy/partial/missing source proofs, mixed formats, limits, tampering and non-executable flags. The CI result must be checked before closing #93.

Parent #92 remains open for real source-side ingestion, PostgreSQL immutable manifests and replay/concurrency proofs, lease fences, durable joins and F-095 physical mirror binding of later checkpoint epochs.
