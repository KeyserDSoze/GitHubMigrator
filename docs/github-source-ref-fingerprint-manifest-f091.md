# F-091 — source ref change fingerprint manifest (initial foundation)

## What is implemented

The domain integrity primitive \`GitHubSourceRefChangeManifestIntegrity\` streams
canonical F-025 ref change evidence and computes a SHA-256 proof, scoped to
the exact source connection and immutable staging snapshot. Each row binds
the canonical F-013 \`[repositoryId, refName]\` key, change kind, previous
and current F-013 SHA-256 fingerprint, and revision. The latter fingerprint
already commits to the normalized source Git object ID without putting
raw OIDs in the receipt.

The PostgreSQL 17 reader \`PostgresGitHubSourceRefChangeManifestReader\`
checks an actual committed F-025 journal and streams only F-025 ref rows
in a repeatable-read transaction ordered by canonical \`source_id\` bytes.
The implementation does not accumulate a ref list in .NET. A zero-change
source epoch still gets a nonzero, snapshot-specific evidence SHA. The
reader checks source ownership and refuses an unavailable/corrupted
staging journal. Domain and PostgreSQL integration tests cover deterministic
replay, changes, deletions, restoration, mismatched source, malformed input,
duplicate/out-of-order keys and cancellation.

## Security and consistency boundary

This is a **read-only foundation**, not yet a new user-facing F-090
checkpoint field or a persisted F-091 ledger. F-090 hashes per-repository
counts, not F-091's per-ref evidence. Existing F-090 receipts and their
versioned digest remain unchanged for backwards compatibility.
No destination ref, GitHub API, GitHub App credential, Git push, sender,
execution grant, automatic retry or migration success is involved.

F-025 F-013 SHA-256 fingerprints attest to what was saved in PostgreSQL;
without re-fetching the source, this is not proof that Azure DevOps has
no newer changes. The next work is to bind F-091 digests to durable,
versioned checkpoint receipts and independently verify provider/source
ref payloads, with safe compatibility for historic F-090 evidence.

## Durable F-090 integration

F-091 now extends newly written F-090 checkpoint receipts with explicit
`evidenceVersion=2`, a persisted serialized ref-change manifest and a
separate SHA-256 attestation over the original F-090 receipt ID, its
original (unchanged) F-090 evidence SHA and the F-091 source ref proof.
Both fields are in the same PostgreSQL transaction as the F-090
checkpoint and append-only audit event. The additive database schema
defaults existing rows to `evidenceVersion=1` and rejects incomplete
version-2 attestations.

On checkpoint history read, the API verifies the bound attestation
and independently derives the ref-change manifest from immutable
PostgreSQL F-025 rows. Missing or corrupted v2 evidence fails closed;
v1 receipts remain readable without falsely claiming fingerprint
attestation. The React Source sync page shows the source ref fingerprint
and bound receipt SHA for v2, and a clear historic label for v1.

The current proof covers *changed refs* in the F-025 journal, not
unchanged refs in the entire source snapshot. It does not authorize or
run any GitHub write, retry, dispatch or migration-completion operation.
