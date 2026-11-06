# F-041 — Real Git create-only transport rehearsal (OFFLINE)

## What works

F-041 executes the first full **real Git CLI transfer of bundled objects**
in a private, disposable local bare destination. It exercises the actual
Git pack/bundle import, atomic create-only ref updates, Git integrity
checks, SHA-1 and SHA-256 object-format selection, and independent
post-transfer ref/OID verification. This is NOT a live GitHub import.

The server-side request must refer to a **fresh, two-person-acknowledged
F-040 human intent** with a current SHA-bound F-039 destination review,
a F-032 exact-ref reconciliation and a F-035/F-038 physically verified
source bundle. The operation is enabled only when explicitly configured.

## Defaults and configuration

```text
GitSourceBundles:Enabled=true
GitSourceBundles:BundleRoot=/trusted/private-bundles
GitImportRehearsal:Enabled=true
GitImportRehearsal:ScratchRoot=/trusted/private-scratch
```

The F-041 feature is **off by default**. Before enabling, provision
dedicated, encrypted, private scratch storage (non-symlinked absolute path),
OS filesystem quotas, a non-root runtime, cleanup monitoring and enough
capacity for at least two temporary bare repositories. The runner's root
is not supplied by the API caller.

The live GitHub destination credential is **not passed to the Git
process**. Its subprocess environment is cleared; only local file
transport is allowed and hooks, system/global Git config, credential
prompt and remote networking are disabled. No `https://`, `ssh://`
or client-provided destination is ever passed to `git push`.

## API (admin/superadmin JWT + completed password change)

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/import-intents/{intentId}/git-rehearsals
{"expectedIntentEvidenceSha256":"<64 lowercase hex>"}
```

Returns `202 Accepted` with a SHA-sealed PostgreSQL rehearsal receipt
for the exact F-040 intent.

```text
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/git-rehearsals/{rehearsalId}
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/git-rehearsals
```

The receipt stores only proof SHA-256s, object format, counts and safe
timestamps. `destinationWritesPerformed=false`,
`executionAuthorized=false` and `readyForExecution=false` are
hardcoded and PostgreSQL CHECK-constrained. "Destination" in this
receipt means the REAL GitHub remote (which was NOT written).
The local disposable target was written and then discarded.

Only **one immutable successful receipt** is stored per F-040
intent. Concurrent replays that produce the same object/ref
evidence may reuse that receipt; altered proofs and actors fail
closed. The database locks on the same source-scoped advisory key
as F-029 through F-040 and rechecks all current proofs, including
physical bundle bytes, before committing.

## Exact CLI behavior

1. Validate F-040 intent as acknowledged, current and unexpired. Ensure
   the F-035 bundle SHA-256 matches PostgreSQL and re-hash every byte.
2. Reconstruct all F-032 ref dispositions from source/target
   immutable pages; validate F-040 safe-ref-plan SHA, counts and exact
   object IDs. `DivergedBlocked` rejects the operation. Maximum
   **128 create candidates** per rehearsal, a resource safety bound.
3. Create unpredictable, private scratch directory. Run `git clone
   --mirror` from the certified bundle to a local bare source;
   run `git fsck --full --strict`, then compare **all** imported source
   ref names and OIDs with the sealed source evidence.
4. Initialize a new disposable bare target with the correct object
   format. Seed only previously **matching** ref objects (same SHA)
   as a simulation of baseline GitHub refs. Branches/tags that exist
   *only* on GitHub cannot be materialized from the source bundle:
   count them as `targetOnlyNotMaterialized` rather than pretending
   their remote bytes have been checked.
5. Run real `git push --atomic`, **only** for `CreateCandidate`
   refs, using `--force-with-lease=<fullRefName>:` for each
   destination ref to require absence. No wildcard, force refspec,
   deletion, rewrite or arbitrary provider URL is allowed.
6. Read ALL ref names and OIDs from the scratch destination and
   require exact equality with the baseline matching refs PLUS new
   refs. Verify `git fsck` on the destination. Re-hash the original
   bundle again before returning the cryptographic receipt.
7. Remove only this request's disposable scratch directory, even
   on failure. The original source bundle remains untouched.

Any object corruption, Git process failure, lease rejection,
ref mismatch, canceled operation, malicious filesystem mapping,
forged digest, or changed intent fails closed and produces no
success receipt.

## Tests

Actual `/usr/bin/git` integration tests create Git commit history
with a branch and annotated tag, export an F-035 bundle, import into
disposable bare repositories, exercise create-only `--atomic` ref
updates, test resulting SHA-256 evidence and absence of scratch
artifacts, and ensure corrupt bundles, divergent refs, forged
plan digests, missing config and symlinked scratch are rejected.
ASP.NET/PostgreSQL integration verifies admin JWT and disabled default,
multi-replica idempotent receipt save, stale source proof checks and
the SQL hard no-destination-write boundary.

## Explicit limitations and next milestone

F-041 **does not contact GitHub for writes** and does not claim to
have verified actual GitHub write scopes, organization ruleset
bypasses, target-only objects, or an atomic remote commit. The
local `--atomic` push shows Git's mechanics but GitHub transport
capabilities still require verified live evidence. It also cannot
predict target drift between the F-032 GET and future import.
Scratch disk capacity and process isolation need operational
hardening for large repositories.

The next increment needs a separate, short-lived, least-privilege
GitHub execution authorization and guarded remote transport with
fresh remote ref lease checks. Real GitHub import must re-hash the
bundle **at write time** and independently verify every target SHA
afterward. Until then every F-029/F-034 workpack stays blocked.
