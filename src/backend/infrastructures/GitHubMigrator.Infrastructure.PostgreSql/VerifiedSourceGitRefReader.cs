using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Reads the exact F-029 source snapshot, NOT the mutable latest staging_assets
// table. The read occurs inside the caller's source-locked transaction.
public sealed class PostgresVerifiedSourceGitRefReader(
    AppDbContext db, IDiscoveryRefManifestStore manifests)
    : IVerifiedSourceGitRefReader
{
    // Historical F-032 baseline reads retain their existing behaviour.
    public Task<VerifiedSourceGitRefs> ReadAsync(
        IncrementalOperationPlan plan, string sourceRepositoryId,
        CancellationToken ct) =>
        ReadCoreAsync(plan, plan.SnapshotId, sourceRepositoryId,
            requireCompleteSealedEpoch: false, ct);

    // F-097b: exact epoch is supplied ONLY by an already-verified F-091
    // checkpoint; unlike legacy F-032, a later snapshot MUST be F-014
    // externalized, completed and fully covered. This remains internal;
    // no HTTP route or destination write uses this method.
    public Task<VerifiedSourceGitRefs> ReadSealedSnapshotAsync(
        IncrementalOperationPlan plan, Guid checkpointSnapshotId,
        string sourceRepositoryId, CancellationToken ct)
    {
        if (plan is null || checkpointSnapshotId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == sourceRepositoryId))
            throw new IncrementalGitRefReconciliationException(
                "SourceRefCheckpointSelectionInvalid");
        return ReadCoreAsync(plan, checkpointSnapshotId, sourceRepositoryId,
            requireCompleteSealedEpoch: true, ct);
    }

    private async Task<VerifiedSourceGitRefs> ReadCoreAsync(
        IncrementalOperationPlan plan, Guid snapshotId,
        string sourceRepositoryId, bool requireCompleteSealedEpoch,
        CancellationToken ct)
    {
        var inventory = await new PostgresDiscoveryRepository(db)
            .GetWithoutRefsAsync(snapshotId, ct);
        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId, ct);
        if (inventory is null || sync is null ||
            inventory.ConnectionId != plan.SourceConnectionId ||
            sync.SourceConnectionId != plan.SourceConnectionId ||
            inventory.Id != snapshotId ||
            inventory.SourceProvider != "azure-devops-services" ||
            sync.JournalVersion != 1 ||
            !inventory.Snapshot.Projects
                .SelectMany(x => x.Repositories)
                .Any(x => x.ProviderId == sourceRepositoryId &&
                    x.VersionControl == VersionControlKind.Git))
            throw new IncrementalGitRefReconciliationException("SourceRefSnapshotUnavailable");

        // ADO inventory is intentionally globally Partial until effective
        // identity/ACL permissions can be verified. That must NOT hide a
        // separately complete F-014 Git ref page chain. Accept only the
        // known non-Git coverage warning; any unknown/project/ref warning,
        // truncation or pagination-loop error refuses the source proof.
        // The F-014 reader below independently requires a Succeeded worker,
        // exact ref count, complete root, canonical OIDs and every chunk SHA.
        if (requireCompleteSealedEpoch &&
            (!inventory.RefsExternalized ||
             inventory.Issues.Any(issue =>
                 issue.Code != "EffectivePermissionsNotVerified")))
            throw new IncrementalGitRefReconciliationException(
                "SourceRefCheckpointCoverageUnverified");

        var selected = new List<GitMirrorRef>();
        string? root = null;
        if (inventory.RefsExternalized)
        {
            if (inventory.Refs.Count != 0 ||
                inventory.RefCount is < 0 or > StagingAssetNormalizer.MaxAssetsPerSnapshot)
                throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
            var manifest = await manifests.GetAsync(snapshotId, ct);
            if (manifest is null || manifest.SourceConnectionId != plan.SourceConnectionId ||
                manifest.AssetCount != inventory.RefCount ||
                manifest.ChunkSize != PostgresDiscoveryRefManifestStore.ChunkSize)
                throw new IncrementalGitRefReconciliationException("SourceRefManifestUnavailable");

            var chunkHashes = new List<string>(manifest.ChunkCount);
            var expectedIndex = 0;
            var counted = 0;
            var cursor = -1;
            while (expectedIndex < manifest.ChunkCount)
            {
                var page = await manifests.ListChunksAsync(snapshotId,
                    cursor, 50, ct);
                if (page.Items.Count == 0)
                    throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                foreach (var descriptor in page.Items)
                {
                    if (descriptor.Index != expectedIndex++)
                        throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                    var block = await manifests.ReadChunkAsync(
                        snapshotId, descriptor.Index, ct)
                        ?? throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                    if (block.Chunk != descriptor ||
                        block.Items.Count != descriptor.AssetCount)
                        throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                    chunkHashes.Add(descriptor.Sha256);
                    foreach (var asset in block.Items)
                    {
                        counted++;
                        if (counted > manifest.AssetCount)
                            throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                        if (asset.RepositoryId != sourceRepositoryId) continue;
                        try
                        {
                            using var identity = JsonDocument.Parse(asset.SourceId);
                            if (identity.RootElement.ValueKind != JsonValueKind.Array ||
                                identity.RootElement.GetArrayLength() != 2 ||
                                identity.RootElement[0].GetString() != sourceRepositoryId)
                                throw new JsonException();
                            var name = identity.RootElement[1].GetString()!;
                            var oid = asset.Properties.GetProperty("ObjectId").GetString()!;
                            var canonical = StagingAssetNormalizer.NormalizeRef(
                                sourceRepositoryId, name, oid);
                            if (canonical.Fingerprint != asset.Fingerprint ||
                                canonical.SourceId != asset.SourceId)
                                throw new JsonException();
                            selected.Add(new GitMirrorRef(name, oid));
                            if (selected.Count > (requireCompleteSealedEpoch
                                ? GitHubSourceCheckpointGitRefProofIntegrity.MaxRefs
                                : GitMirrorRefReconciler.MaxRefsPerSide))
                                throw new IncrementalGitRefReconciliationException("SourceRefSelectionLimit");
                        }
                        catch (Exception ex) when (ex is JsonException or
                            InvalidOperationException or KeyNotFoundException or
                            StagingSyncInputException)
                        {
                            throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
                        }
                    }
                }
                cursor = page.Items[^1].Index;
                if (page.NextAfter is null && expectedIndex != manifest.ChunkCount)
                    throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
            }
            // A manifest with zero chunks has an explicitly hashed empty root.
            root = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes("ref-manifest-v1\n" +
                    string.Join("\n", chunkHashes) + "\n")));
            if (counted != manifest.AssetCount ||
                expectedIndex != manifest.ChunkCount || root != manifest.RootSha256)
                throw new IncrementalGitRefReconciliationException("SourceRefManifestMismatch");
        }
        else
        {
            // Legacy inline snapshots have no sealed F-014 manifest.
            // A globally partial inventory cannot prove absent source refs.
            if (!sync.SourceInventoryComplete ||
                inventory.Completeness != DiscoveryCompleteness.Complete)
                throw new IncrementalGitRefReconciliationException("SourceRefCoverageUnverified");

            // PostgreSQL projects only the selected repository's refs; do
            // not materialize the full snapshot's large JSON ref array.
            var rows = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_build_array(r ->> 'name', r ->> 'objectId')::text AS "Value"
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_array_elements(d.inventory -> 'refs') r
                WHERE d.id = {snapshotId}
                  AND d.connection_id = {plan.SourceConnectionId}
                  AND r ->> 'repositoryId' = {sourceRepositoryId}
                ORDER BY r ->> 'name'
                LIMIT {GitMirrorRefReconciler.MaxRefsPerSide + 1}
                """).ToArrayAsync(ct);
            if (rows.Length > GitMirrorRefReconciler.MaxRefsPerSide)
                throw new IncrementalGitRefReconciliationException("SourceRefSelectionLimit");
            foreach (var json in rows)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var values = doc.RootElement;
                    if (values.ValueKind != JsonValueKind.Array ||
                        values.GetArrayLength() != 2)
                        throw new JsonException();
                    selected.Add(new GitMirrorRef(
                        values[0].GetString()!, values[1].GetString()!));
                }
                catch (Exception ex) when (ex is JsonException or
                    InvalidOperationException)
                {
                    throw new IncrementalGitRefReconciliationException("SourceRefSnapshotUnavailable");
                }
            }
        }
        if (requireCompleteSealedEpoch &&
            (selected.Count == 0 ||
             selected.Any(x => x.ObjectId.Length != selected[0].ObjectId.Length)))
            throw new IncrementalGitRefReconciliationException(
                "SourceRefCheckpointObjectFormatInvalid");

        return VerifiedSourceGitRefIntegrity.Seal(snapshotId,
            plan.SourceConnectionId, sourceRepositoryId, root, selected);
    }
}
