# F-062 — lease-fresh source mirror proof receipts

F-061 serializes local Git source materialization across replicas with
PostgreSQL fencing, but a content-identical old F-060 receipt could still
pass its two-minute freshness check during a newly acquired lease.
F-060 deliberately keeps the physical object-set evidence SHA stable across
re-verification, and that SHA does not include the proof timestamp.

## Change

1. F-060 keeps its stable `EvidenceSha256` for immutable bundle/ref/object
   equality. It also emits `VerificationReceiptSha256`, a separate SHA-256
   over that evidence and the UTC round-trip timestamp, domain-separated
   with `f062-source-mirror-verification-receipt-v1`.
2. F-061 validates both hashes. Editing `VerifiedAtUtc` on an existing
   proof without resealing it fails with `SourceMirrorLifecycleProofInvalid`.
3. After locking PostgreSQL and reloading the live lease row, F-061 checks
   that `VerifiedAtUtc >= UpdatedAtUtc` (the current lease's admission
   instant). An intact receipt from an earlier generation is rejected as
   `SourceMirrorLifecycleProofStale`; the owning worker quarantines it
   rather than silently reusing it. A stale worker cannot quarantine the
   successor, because the existing token/generation/deadline fence still
   runs first.

The timestamp is not put into the stable SHA recorded as the first
verified physical object-set digest. Thus normal F-060 re-verification
of identical bytes across generations remains possible without rewriting
the immutable initial digest. Neither the random DB lease token nor a GitHub
credential is persisted in the additional receipt SHA.

## Boundaries and validation

This is a **freshness and accidental-replay check**, not a cryptographic
attestation that a hostile in-process implementation executed `git fsck`.
The sealing function is available inside the trusted application process.
F-060 still performs independent bundle hashing, complete ref matching and
full Git object-closure validation under a shared POSIX filesystem lock.
The durable journal and remote coordinator remain internally gated and
default-disabled. No remote push, GitHub write grant, migration completion,
new API or background retry is introduced.

Regression coverage includes domain tests for altered timestamp/checksum,
pre-lease receipts, equal content with different receipt SHA; PostgreSQL
integration tests reuse the exact old proof after a new lease and verify
fail-closed quarantine, immutable original object digest and audit history.
Run the full .NET, PostgreSQL real-Git, Playwright and Terraform CI gates.
