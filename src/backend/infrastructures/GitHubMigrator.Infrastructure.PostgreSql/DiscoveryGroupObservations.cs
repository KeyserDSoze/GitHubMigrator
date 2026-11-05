namespace GitHubMigrator.Infrastructure.PostgreSql;

// Sanitized, checkpoint-provenanced Graph group records. Raw group metadata is
// evidence only: it never implies inheritance, effective access or a grant.
public sealed class DiscoveryGroupObservationRecord
{
    public Guid JobId { get; set; }
    public string SourceId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ResourceKey { get; set; } = "security-groups";
    public int PageIndex { get; set; }
}
