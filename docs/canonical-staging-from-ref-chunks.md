# F-014 — Canonical PostgreSQL staging directly from verified ref chunks

**Status: implemented first chunk-driven canonical staging path.** A durable `DiscoverSource` job can update the authoritative F-013 staging catalog from **bounded Git ref chunks**, rather than loading a second copy of its full ref collection and normalizing it all into a .NET dictionary.

This extends [per-page ref ingestion](streamed-ref-observations.md) and [immutable ref manifests](immutable-ref-manifests.md).

## End-to-end flow

1. A source discovery worker saves its immutable discovery JSONB snapshot and provisional Git ref page observations under lease-fenced checkpoints.
2. The worker prepares the F-014 ref manifest. Preparation compares the exact multiset of `(repository ID, name, object ID)` in the source snapshot against PostgreSQL provisional observations, and seals ordered chunks with SHA-256 digests.
3. The staging service requests a **slim PostgreSQL projection** of the discovery snapshot. PostgreSQL replaces the large `refs` JSONB array with `[]` *before transmission to .NET*. All original snapshot metadata, security evidence, user/group records and project/repository definitions remain available.
4. F-013 normalizes **non-ref** assets exactly as before; ref assets are excluded from this in-process normalization.
5. The staging repository acquires its existing source-scoped PostgreSQL transaction lock, validates the live source-job lease and verifies the prepared root manifest against its stored chunk descriptors.
6. It reads **one 250-ref chunk at a time** by `(job_id, source_id)` key range, revalidates each row's canonical ID, JSONB object ID, fingerprint and chunk digest, and applies it through the existing F-013 batched UPSERT.
7. Canonical additions, changes, reappearances, revision increments, missing detection, and the persisted staging summary all run inside **one source-scoped database transaction** shared with the non-ref updates.
8. After all chunks are applied, the repository checks the live lease again before committing. Only then does the durable job transition to `Succeeded`.

No `StageSnapshot` job or legacy direct `POST /api/staging/sync` behavior is changed: the existing F-013 full-snapshot normalizer remains available for compatibility.

## Semantic guarantees

- **No false deletions from partial inventories:** `ref` assets are marked missing only when source discovery completeness is `Complete`.
- **F-013 parity:** changed SHA or reappearing missing ref increments `revision`; unchanged ref does not. Every present ref updates `last_seen_snapshot_id`. The source snapshot's timestamp controls ordering.
- **Idempotent replay:** repeated application of an already committed immutable snapshot returns the same persisted result, without further revisions.
- **Atomic rollback:** if any later chunk fails checksum, row-payload verification or lease fencing, **all earlier staging changes in that transaction roll back**, including project/repository rows.
- **Bounded ref memory:** only a single chunk's 250 normalized refs is held in process memory by this staging path. The PostgreSQL source projection does not return the large ref array to .NET.
- **Exact accounting:** the returned `observed` count includes both non-ref assets and the verified manifest's ref count; changed/added/missing figures derive from indexed canonical rows.
- **Safety:** this pipeline never calls GitHub, modifies Azure DevOps or approves a migration plan. The manifest is read under a valid worker lease; public manifest APIs still require a successful job.

## Tests

- PostgreSQL: stage 1,250 refs, then a second full snapshot with one ref changed and one omitted; assert canonical OID, revision, missing status, unchanged count, idempotency and bounded catalog pagination.
- PostgreSQL: later partial scan omits a ref but leaves its canonical row active and unchanged.
- PostgreSQL: payload corruption in a later prepared chunk fails and rolls back all canonical operations.
- PostgreSQL: expired worker cannot publish the canonical catalog.
- Existing .NET, API/JWT/PostgreSQL, Playwright and Terraform CI continue to execute.

## Remaining scalability work

This **does not yet achieve constant-memory *source discovery***. The Azure DevOps reader still accumulates the full inventory and the original snapshot is stored as one JSONB document. Other asset families are still normalized in memory during staging. We have removed the large Git ref array from the **second (staging) half** of a successful durable discovery, not from the provider-side collection.

Next: switch to chunk-referenced source manifests in place of the full JSONB `Refs` property, stream other asset families, implement source-side delta collection and scheduled synchronization with backpressure. Production schema migrations, retention/compaction, and reproducible memory/throughput benchmarks are still required. Do not claim million-ref performance until measured.
