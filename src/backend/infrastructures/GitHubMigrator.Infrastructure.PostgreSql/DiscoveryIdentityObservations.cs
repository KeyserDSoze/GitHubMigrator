namespace GitHubMigrator.Infrastructure.PostgreSql;

// Provisional Graph observations share source hashes with F-013 staging, but
// never claim that an incomplete source scan is an authoritative identity list.
public sealed class DiscoveryIdentityObservationRecord
{
    public Guid JobId { get; set; }
    public string SourceId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ResourceKey { get; set; } = "graph-users";
    public int PageIndex { get; set; }
}
