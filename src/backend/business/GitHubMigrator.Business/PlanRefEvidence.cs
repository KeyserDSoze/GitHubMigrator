namespace GitHubMigrator.Business;

// Snapshot-scoped F-005 reference counts. Never query the latest mutable
// canonical staging catalog for a historical migration-plan draft.
public interface IPlanRefEvidenceReader
{
    Task<IReadOnlyDictionary<string, int>> CountByRepositoryAsync(
        Guid snapshotId, CancellationToken ct);
}
