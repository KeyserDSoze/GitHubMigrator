# F-076 — source-rebuilt and GitHub-observed read-only dispatch orchestration

## Motivation and previously separated safeguards

F-075 made a strong PostgreSQL-authoritative check of the current F-045,
F-047, F-050, F-052 and F-055 authorization/retry records. Its F-074
input was still two **caller-supplied in-memory objects**: the F-044
create-only candidate packet and the F-031 typed GitHub ref observation.
Neither alone can be accepted as fresh source or remote evidence.

F-076 adds a **server-only read-only orchestration boundary**. It
combines the existing F-044 source regeneration, F-072/F-073
authenticated two-pass GitHub GET reader and F-075 PostgreSQL verifier.
There is no new token, URL, refspec, Git command, outbound write,
database schema, production API endpoint or DI registration.

## F-076 flow

1. Accept only the preparation, plan, scope, F-055 hazard and
   independent actor IDs plus a prior sealed F-051 read-only preflight
   receipt. No caller-supplied ref names, Git OIDs, destination URL,
   credential, private signing key or Git command is accepted.
2. Require explicit test-repository allowlists, enabled feature
   switches, positive GitHub App and installation IDs and a valid F-051
   proof format. If configuration or immutable packet identifiers do
   not match, **stop before reading the GitHub App signing key**.
3. Call **the real F-044** `GitHubCreateOnlyPacketService` using its new
   `IGitHubCreateOnlyPacketEvidenceReader` interface. This
   reconstructs candidate names/OIDs from PostgreSQL reconciliation
   and workpack data, verifies the source Git bundle bytes **twice**
   and checks the source scope, plan, human intent and ref fence.
4. Only then resolve the server-configured private-key reference
   through `IConnectionSecretStore`. Call the already implemented
   `IGitHubRemoteAppPreflightReader.InspectAsync` with an
   **18-second deadline** and the reconstructed F-044 candidate plan.
   Production reader F-072/F-073 signs the GitHub App JWT,
   validates installation owner/App ID and active state,
   mints a one-repository installation token, checks numeric target
   identity, GETs heads, tags and candidate policies, then GETs refs
   and identity again. No URL/token/refspec is taken from caller.
5. Re-seal the returned full **typed** F-031 refs, require exact
   organization/repo/number, canonical ref ordering, SHA-256 digest,
   complete F-051 baseline and no collisions with any create-only
   candidate. Re-evaluate the candidate-specific policies with
   F-046; any active, ambiguous or unverified rule **blocks**.
   Time windows and token expiry are checked before continuing.
6. Rebuild F-044 **a second time after all GitHub I/O**: any change in
   source bundle, provenance, packet SHA or canonical candidates
   refuses the proof.
7. Call the concrete F-075
   `PostgresGitHubRemoteDispatchReviewVerifier` through its narrow
   `IGitHubRemoteDispatchAuthorityVerifier` interface. F-075
   acquires both repository advisory lock families, samples PostgreSQL
   server time, rereads the five control-plane authority records
   and append-only audit events, then seals the inert F-074 receipt.

The output remains strictly
`ReviewedWriteDisabled` with all of
`RemoteTransportInvoked`, `RemoteWritesAuthorized`,
`MayAutomaticallyRetry` and `MigrationComplete` set to **false**.

## Test coverage

The new orchestration test fixture scripts the source evidence reader,
secret store, provider observation and authority verifier independently,
and asserts:

- Exactly **two** full source packet regenerations, **one**
  server-configured secret read, **one** provider preflight and
  **one** F-075 authority read on the valid path.
- Wrong packet, repository, original policy, preflight status, feature
  switches or repository allowlist refuse **before any secret access**.
- GitHub App installation/repository identity mismatch, mutated branch,
  moved **annotated tag object** OID, newly occupied candidate,
  new target-only ref, invalid ref hash, active/unknown rules or token
  expiry refuse **before consulting PostgreSQL for a green receipt**.
- Any source packet or candidate change during provider GETs refuses
  even if the earlier packet had been valid.
- Missing signing key cannot trigger token mint; F-075's persisted
  authority veto is propagated without changing its safety semantics.
- The output contains no remote ref names, secrets or signing key.

The underlying F-072/F-073 HTTP-handler tests still verify the real
fixed-host GitHub endpoint/GET ordering and token restrictions. F-075's
real PostgreSQL tests still verify lock contention, race conditions
and durable audit evidence. No real GitHub account is contacted by CI.

## Important security limits

**This is an unregistered, internal service**, not an API route or
production transfer worker. The previous F-051 preflight receipt is a
server-provided proof that must remain cryptographically bound to the
persisted F-052 grant; F-075 verifies the matching digest and epoch.
Its *full* original remote observation and token-expiration claims are
not independently persisted in the current schema, so F-076 does NOT
claim to reconstruct the entire historic F-051 receipt from
PostgreSQL. That is a separate persistence hardening prerequisite.

These GETs do not lock GitHub itself. The target could change after
the final GET, or an observation may change and revert between GETs.
Even the successful F-075 review receipt remains **non-executable**:
no privileged sender, write grant, conditional ref creation, remote
Git object closure audit or migration-complete state has been
authorized. A future production writer must separately authenticate
F-051's historic evidence, fence all control-plane state,
perform truly non-destructive create-only operations and
quarantine uncertain sends without retries.

No schema/index migrations or GitHub credentials exposed.
F-076 is stacked on F-075 PR #76; `main` stays unchanged.
CI: .NET10/xUnit, PostgreSQL17 real Git, Playwright, Terraform.
