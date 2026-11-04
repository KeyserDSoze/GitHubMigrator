using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Provisional observations committed together with the immutable source page.
// Unlike staging_assets, these do not represent the latest approved source
// catalog and can never imply an absent ref was deleted.
public sealed class DiscoveryRefObservationRecord
{
    public Guid JobId { get; set; }
    public string SourceId { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ResourceKey { get; set; } = "";
    public int PageIndex { get; set; }
}
