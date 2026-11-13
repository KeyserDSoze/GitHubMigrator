# F-035 — Bounded offline Git source bundle acquisition

## Purpose and operational boundary

This increment generates **actual Git object bundles** from an
operator-provisioned, local, bare source mirror. It is NOT a remote
Azure DevOps fetch/import and is NOT a destination GitHub writer.

The F-029 operation plan, F-014 source snapshot, F-032 ref reconciliation
and F-034 durable workpack must all agree and remain current. Only a
workpack in the terminal `Blocked / WriteBlocked` phase can request
a source export. The write barrier remains in force.

## Configuration — disabled by default

```text
GitSourceBundles:Enabled = true
GitSourceBundles:SourceRoot = /trusted/readonly/source-mirrors
GitSourceBundles:BundleRoot = /trusted/persistent/bundles
```

The two roots must exist, be absolute and have no symlinked ancestors.
Provision mirrors using the secure operator-side source credential
workflow (not implemented by this feature). Both roots should be
mounted from dedicated, access-controlled Linux volumes. Source mirrors
should be read-only to the migrator service; only the bundles volume
requires write permission. Encryption at rest, storage capacity,
retention policies and approved cleanup/GC require deployment design.

A source bare mirror **must** be located at:

```text
<SourceRoot>/<sourceConnectionId as lowercase 32-hex>/<SHA256 UTF8(sourceRepositoryId)>.git
```

The source connection ID and repository ID come exclusively from the
server-proven immutable operation plan. The HTTP API never accepts a
filesystem path, Git argument, remote URL or source PAT.

F-035 consumes local bare mirrors; it has no authenticated Azure DevOps
Git protocol client. Git CLI operations are restricted to
`for-each-ref`, `fsck`, `bundle create`, `bundle verify` and
`bundle list-heads`. The subprocess uses a fixed executable
`/usr/bin/git`, argument arrays (no shell), a cleared environment,
disabled prompts and protocol transport, no hooks or replace objects.
External object alternates, symbolic links in mapped roots and hidden
refs fail closed.

## Endpoint

Admin/superadmin JWT (with changed password) only:

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/source-bundle
Content-Type: application/json

{"expectedReconciliationSha256":"<64 lowercase hex>"}
```

The matching `GET` route returns stored proof metadata without the
Git binary or an absolute server path. A missing or stale plan, changed
reconciliation, mismatched source refs or disabled root returns a safe
error. The operation runs synchronously; expensive exports require
later durable async scheduling for production.

## Exact Git checks

1. Canonicalize the complete F-014/F-032 source refs (at most 25,000).
2. Enumerate **all** local refs and compare exact names + full SHA-1 or
   SHA-256 object IDs, including tags. Unexpected notes, aliases and
   hidden branches are rejected.
3. Run `git fsck --full --strict --no-reflogs --no-progress` against
   the bare source object store.
4. Generate `git bundle create <random-temp-file> --all`.
5. Run `git bundle verify` and compare `bundle list-heads` back to
   the F-032 source proof; `HEAD` pseudo-ref is ignored.
6. Reject outputs above 512 MiB and use a 65-second timeout per command.
   Bound Git console output to 16 MiB; discard stderr rather than
   exposing paths or environment.
7. SHA-256 hash all binary bytes and atomically move to
   `<BundleRoot>/<workpackId-as-32hex>.bundle`. Existing bundle files
   are not overwritten.
8. Revalidate all F-029/F-032/F-034 proofs under PostgreSQL's original
   source advisory lock. Save once into `git_source_bundle_evidence`:
   UUIDs, hashes, ref count, byte count, filename and acquisition time.
   No raw Git pack, URI, file path, secret or GitHub destination grant
   is stored in PostgreSQL.

The database enforces a filename pattern, 512 MiB size, SHA-256 shape,
ref-count ceiling and `ready_for_execution=false`. The constraint is
retrofitted when EF's `EnsureCreated` had already created a table, as
well as when bootstrap creates a new table.

## Proof freshness and limitations

The DB SHA-256 binds the **observed file at export time**. A later user
of the bundle must rehash the actual file before importing; a stored
digest is not evidence that a mutable local file remains unchanged.
A current planning proof is not write authorization and does not
guarantee atomic cross-provider state.

A second request for an already persisted proof returns existing
metadata without re-running Git. If a process dies after atomically
promoting a bundle but before the database transaction, the leftover
file is **not** considered an approved checkpoint; it is quarantined
by the no-overwrite policy until explicit verification/cleanup is
implemented. This conservative failure is intentional.

Successful F-035 tests execute real `git` on Linux, generate a bundle,
clone the bundle into an empty bare repository, verify commit, tag and
file content; reject unexpected refs, source-OID drift, symlink paths,
disabled configuration and duplicate exports. ASP.NET+PostgreSQL tests
cover JWT, disabled mode, source-bound immutable metadata, replay,
SQL no-write constraints and zero provider calls.

## Remaining work

Before GitHub synchronization can be advertised as functioning:
authenticated read-only Azure DevOps Git remote acquisition, durable
bounded async acquisition and garbage collection, hard resource
isolation (separate OS account/container, disk quota), filesystem
rehashing on replay, complete SHA-256/sha1 integrity across transfer,
policy-based destination approval, non-destructive write-capability
checks, GitHub repository import/push and post-import refs/object
verification. No step in F-035 transfers data to GitHub.
