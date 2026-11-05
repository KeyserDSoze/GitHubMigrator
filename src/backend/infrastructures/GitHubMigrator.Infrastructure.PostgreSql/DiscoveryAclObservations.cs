namespace GitHubMigrator.Infrastructure.PostgreSql;

// Sanitized read-only source ACL ACE, bound by FK to an immutable checkpoint.
// No effective permission, inherited decision or GitHub access grant.
public sealed class DiscoveryAclObservationRecord
{
    public Guid JobId { get; set; }
    public string SourceId { get; set; } = "";
    public string SecurityToken { get; set; } = "";
    public string SecurityDescriptor { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ResourceKey { get; set; } = "";
    public int PageIndex { get; set; }
}
