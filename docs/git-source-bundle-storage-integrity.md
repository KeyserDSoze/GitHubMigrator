# F-038 — Bundle storage integrity, orphan recovery and conservative cleanup

## Why

F-035/036/037 create a real Git bundle, persist its SHA-256 in PostgreSQL,
and recover durable source-acquisition checkpoints after a worker crash.
An immutable DB digest alone does **not** prove that bytes on disk are
still present and unchanged. A worker can also die **after** the final
bundle is atomically promoted but **before** the DB evidence transaction.

F-038 closes these gaps without creating any GitHub writer.

## Mandatory verification before reuse

The new `IGitSourceBundleArtifactVerifier` binds a stored PostgreSQL
workpack UUID to exactly `<workpackId-as-32hex>.bundle` inside the
server-configured bundle root. It never accepts a filesystem path from
the HTTP client. It opens and reads the entire file under a bounded
(512 MiB) length check, rehashes SHA-256 and compares byte length and
hash against the immutable F-035 evidence. Missing, truncated, modified,
symlinked and unreadable files fail with closed safe codes.

F-035 `GitSourceBundleService.AcquireAsync` now verifies **both**:
- an existing file being reused from a prior PostgreSQL checkpoint;
- the output immediately after an acquisition is committed.

F-036 `AzureGitSourceAcquisitionService` verifies the saved bytes when
returning an already-acquired artifact; the F-037 worker therefore
cannot report source BundleVerified based solely on a stale disk file.

The original GET API still returns immutable *metadata*, and its
`isCurrent` field refers to plan/evidence freshness, not disk health.
To explicitly inspect the live file, use the new admin-only, read-only:

```http
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/source-bundle/storage-integrity
```

Successful result includes workpackId, verified digest, bytes, verifiedAtUtc,
isCurrent and `readyForExecution=false`. A hash mismatch produces an
HTTP 409 with `BundleStoredDigestMismatch`, not a fabricated success.
The path is never included in the response.

## Crash after promotion but before DB commit

The on-disk bundle filename is deterministic from the workpack UUID.
When F-035 sees this file and no persisted checkpoint, it no longer
immediately rejects the job as `BundleAlreadyExists`:
1. Obtain an exclusive, shared-volume POSIX file lock.
2. Validate source mirror refs and objects using the original F-014/F-032
   proof and Git fsck.
3. Ensure the old final artifact is not a symlink, bound its size, and
   compute its entire SHA-256.
4. Run `git bundle verify` and verify **all** branches/tags and full
   object IDs against the current source proof.
5. Return those bytes as the recovered artifact. The existing F-035
   PostgreSQL store independently revalidates F-029/F-032/F-034 under
   the source advisory lock and either inserts its append-only evidence
   or rejects a conflicting digest.

A verified orphan is **never overwritten**. A corrupt or mismatching
orphan is **not auto-deleted**: an operator must quarantine/review it.

## Storage budget and cleanup

`GitSourceBundles:MaxStorageBytes` configures a total local bundle-root
budget in bytes, defaulting to **4 GiB**. The store counts all root
files, even unfamiliar ones; it rejects symlinks and unexpected
directories. A local cross-process `.git-bundle-writes.lock` serializes
admitted exports across workers on a shared filesystem supporting
advisory locking. It checks the aggregate budget before and after
bundle generation, before atomic promotion. Individual bundle size
remains limited to 512 MiB. A full store fails closed with
`BundleStoreQuotaExceeded`.

This is an **application-level local-directory quota**, not a hard OS
disk-quota or durable multi-volume storage reservation. Remote NFS
lock reliability, per-tenant isolation, disk free-space monitoring and
replicated-volume orchestration still require deployment controls.
Deploy with dedicated quota-enforced volumes and sufficient headroom.

`LocalGitBundleArtifactStore.CleanupStaleTempsAsync` is a **manual,
non-HTTP capability**. It requires an exclusive write lock and at least
24 hours of inactivity, deletes at most 100 matching temporary files
per invocation, and only recognizes exactly
`<32-lowercase-hex>.<32-lowercase-hex>.tmp`. It never touches a
`.bundle`, unknown file or current temp. It is deliberately not
auto-scheduled; human operators must approve retention/cleanup policy.

## Tests and remaining work

Real Git integration tests check bundle replay after a simulated
post-promotion/pre-DB crash, rehashing, equal-length corruption,
missing files, symlink substitution, quota exhaustion, and protected
stale-temp cleanup. The ASP.NET/PostgreSQL17 regression now verifies
that a forged database-only proof without a physical artifact cannot
be reported as verified on an acquisition retry and that the storage
check API is JWT-gated.

No GitHub import, push, ref update or migration authorization exists.
Before a destination executor can be enabled, require independent
policy-based approval, rehash at the **moment of import**, Git object
reachability checks, destination write-scope preflight, multi-volume
safe cleanup and post-write GitHub SHA validation.
