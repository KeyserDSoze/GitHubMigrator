using System.Data;
using System.Runtime.CompilerServices;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-091 source-only proof reader. The F-025 journal and its summary are
// observed in one REPEATABLE READ snapshot; no migration send/credentials.
public sealed class PostgresGitHubSourceRefChangeManifestReader(
    AppDbContext db, IStagingRepository staging)
{
    public async Task<GitHubSourceRefChangeManifest> ReadAsync(
        Guid sourceConnectionId, Guid snapshotId, CancellationToken ct)
    {
        if (sourceConnectionId == Guid.Empty || snapshotId == Guid.Empty)
            throw Refused("SourceRefChangeManifestInvalid");

        // F-090 owns a source-locked transaction during checkpoint creation.
        // Standalone probes use REPEATABLE READ; nested transactions are forbidden.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, ct)
            : null;
        var epoch = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId &&
                x.SourceConnectionId == sourceConnectionId, ct);
        if (epoch is null || epoch.JournalVersion != 1)
            throw Refused("SourceRefChangeManifestSnapshotUnavailable");

        StagingChangeSummary summary;
        try
        {
            summary = await staging.GetChangeSummaryAsync(snapshotId, ct)
                ?? throw Refused("SourceRefChangeManifestSnapshotUnavailable");
        }
        catch (StagingChangeJournalUnavailableException)
        { throw Refused("SourceRefChangeManifestSnapshotUnavailable"); }
        catch (StagingChangeIntegrityException)
        { throw Refused("SourceRefChangeManifestJournalCorrupt"); }

        if (summary.SourceConnectionId != sourceConnectionId ||
            summary.SnapshotId != snapshotId)
            throw Refused("SourceRefChangeManifestJournalCorrupt");

        // SQL streams only bounded-size fingerprint metadata in canonical
        // byte order; no ref/OID array is materialized in the API process.
        async IAsyncEnumerable<StagingChangeView> Rows(
            [EnumeratorCancellation] CancellationToken token)
        {
            var query = db.StagingAssetChanges.FromSqlInterpolated($"""
                SELECT * FROM staging_asset_changes
                WHERE source_connection_id = {sourceConnectionId}
                  AND snapshot_id = {snapshotId}
                  AND asset_type = 'ref'
                ORDER BY source_id COLLATE "C"
                """).AsNoTracking().AsAsyncEnumerable();

            await foreach (var change in query.WithCancellation(token))
                yield return new StagingChangeView(change.AssetType,
                    change.SourceId, change.Kind,
                    change.PreviousFingerprint,
                    change.CurrentFingerprint, change.Revision);
        }

        var manifest = await GitHubSourceRefChangeManifestIntegrity
            .ComputeAsync(sourceConnectionId, snapshotId, Rows(ct), ct);

        // The global journal also includes repositories, users, groups,
        // etc. A ref-family count must not exceed any global kind count.
        if (manifest.Added > summary.Added ||
            manifest.Changed + manifest.Restored >
                (long)summary.Changed + summary.Restored ||
            manifest.Missing > summary.Missing)
            throw Refused("SourceRefChangeManifestJournalCorrupt");

        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return manifest;
    }

    private static GitHubSourceRefChangeManifestException Refused(string code)
        => new(code);
}
