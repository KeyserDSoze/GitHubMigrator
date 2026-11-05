namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-025: immutable per-snapshot change history written atomically with F-013
// catalog UPSERTs. No credential, ACL payload, Git object bytes or target write.
public sealed class StagingAssetChangeRecord
{
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public string AssetType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? PreviousFingerprint { get; set; }
    public string? CurrentFingerprint { get; set; }
    public int Revision { get; set; }
}
