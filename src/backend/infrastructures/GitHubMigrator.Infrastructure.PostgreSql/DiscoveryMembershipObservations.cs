namespace GitHubMigrator.Infrastructure.PostgreSql;

// Immutable, page-provenanced direct Graph edges. No inferred membership,
// effective permission, or destination GitHub access grant is stored.
public sealed class DiscoveryMembershipObservationRecord
{
    public Guid JobId { get; set; }
    public string SourceId { get; set; } = "";
    public string GroupDescriptor { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ResourceKey { get; set; } = "";
    public int PageIndex { get; set; }
}
