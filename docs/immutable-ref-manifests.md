# F-014 — Immutable chunk manifests for source Git refs

**Status:** the F-014 provisional PostgreSQL ref observations can be finalized into an immutable, verifiable **manifest of ordered chunks**, following F-008 snapshot collection. It is the next bounded-memory *reading and verification* slice, not yet a replacement for the full JSONB discovery snapshot.

## How it runs

1. Azure DevOps discovery collects GET-only ref pages; each page commits up to 1,000 sanitized ref records, normalized in 250-row PostgreSQL batches, together with its F-008 checkpoint.
2. The worker completes and commits its immutable discovery snapshot. A leased worker verifies the saved snapshot contains exactly the same **(repository ID, ref name, object ID)** multiset as the provisional ref table, using a PostgreSQL `EXCEPT ALL` comparison. A matching total count alone is **not sufficient**.
3. Under the active worker lease and a PostgreSQL transaction, the manifest builder streams the provisional ref table in **source-ID order**, holding only 250 source IDs/fingerprints at a time, plus compact descriptors (one per chunk).
4. Each chunk records its index, first and last source ID, number of assets and SHA-256 of the canonical ordered `(sourceId, fingerprint)` pairs. A versioned root digest combines the chunk hashes. Descriptors and the manifest commit transactionally.
5. The existing F-013 normalization commits the canonical staging catalog. Only after the durable discovery job transitions to `Succeeded` do the manifest and its chunks become visible through the REST API. A failed attempt never exposes a prepared manifest.
6. On retry, a previously prepared manifest for the same immutable job snapshot and asset count is reused. Existing source snapshots and ref observations cannot silently mutate the prepared chunk definitions.
7. When retrieving a chunk, PostgreSQL selects only the source-ID range and at most 251 rows via the `(job_id, source_id)` index. The reader recomputes its hash and refuses to serve missing, added, or altered records.

## PostgreSQL

- `discovery_ref_manifests`: one row per discovery job, source ID, asset/chunk count, fixed chunk size (250), root SHA-256, preparation timestamp; FK to the durable migration job.
- `discovery_ref_chunks`: one row per ordered chunk index, count, first/last source ID, SHA-256; FK to its manifest.
- `discovery_ref_observations`: existing provisional normalized ref records; its `(job_id, source_id)` primary key makes range reads index backed.
- The manifest is prepared with a fenced live lease and is not returned until the parent job is marked successful.

## APIs

All routes inherit the existing `admin`/`superadmin` + completed-first-login-password authorization.

| Method | Route | Outcome |
| --- | --- | --- |
| GET | `/api/jobs/{jobId}/ref-manifest` | Job, asset count, chunk count, fixed size, root digest. `409` when not completed, `404` when absent. |
| GET | `/api/jobs/{jobId}/ref-manifest/chunks?after=-1&take=20` | Up to 50 ordered chunk descriptors; keyset `nextAfter` for continuation. |
| GET | `/api/jobs/{jobId}/ref-manifest/chunks/{index}` | One verified chunk of at most 250 metadata-only ref records; `409` if checksum or count disagrees. |

There is no provider access on these GET routes and no way to use them to approve a plan or publish GitHub data. The Discovery page shows the number of sealed chunks and a short prefix of the digest when an asynchronous scan finishes.

## Reliability and security guarantees

- Partial or failed discovery jobs cannot expose a manifest; a snapshot and its provisional records must match before preparation.
- Lease expiry rejects manifest preparation. Sealing and the compact descriptor rows commit in a single PostgreSQL transaction; repeated preparation does not insert duplicates.
- Chunk content is loaded only for the requested keyset range and rehashed. No secrets, provider continuation tokens, or raw request bodies are returned.
- A SHA-256 digest helps detect **unexpected corruption or inconsistencies**. It is **not** a digital signature or an independent proof against a privileged attacker who can rewrite both data and digests.
- An empty source ref set produces a deterministic root digest and zero chunks.
- The chunk schema is additive in the current development bootstrap; production-grade reviewed schema migrations and deployment plans remain required.

## Tests

- Real PostgreSQL: build five chunks from 1,250 ref observations, page through descriptors, verify each range and root, check idempotent preparation and successful-job visibility.
- Reject a mismatch even when source snapshot and observation counts agree but an object ID differs.
- Reject missing observations, expired lease, invalid page sizes and out-of-range chunk indices.
- Tamper with a stored fingerprint and ensure a chunk read fails closed rather than serving silently inconsistent data.
- Full ASP.NET + PostgreSQL: the live Azure DevOps source scan generates a manifest, checks JWT/RBAC, retrieves a verified chunk and never contacts external providers on manifest reads.
- Playwright: the Discovery page shows the completed manifest after the asynchronous scan.

## Canonical staging consumer (next slice)

The durable `DiscoverSource` worker now **consumes this manifest directly** to update the F-013 canonical staging catalog. PostgreSQL strips the full refs array before returning the source snapshot to the staging service; each 250-ref chunk is read, reverified and UPSERTed under one source-scoped transaction with non-ref assets. See [canonical staging from chunks](canonical-staging-from-ref-chunks.md).

## Limitations

**Full end-to-end constant-memory discovery is not yet implemented.** The Azure DevOps reader still creates the complete `DiscoveryInventory` and the legacy F-013 staging normalizer still loads the snapshot and all normalizable assets in RAM. Manifest *preparation and reading* are bounded by chunk size, but the workflow as a whole is not. Also, manifests currently cover **Git refs only**, not repository/TFVC history, Boards, pipeline definitions, ACLs, or blobs.

Next: introduce chunk-referenced source snapshot manifests, stream all asset types through incremental normalized staging, add delta polling and compaction, run load tests with reproducible memory/throughput measurements, and then implement reviewed F-005 approval gates before GitHub write workers.
