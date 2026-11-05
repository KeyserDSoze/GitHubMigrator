using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-005 slice 1: a deterministic, read-only plan review. Nothing is persisted or approved.
// ACL evidence is deliberately NOT converted into effective GitHub grants.
public sealed record PlanSelectionInput(string SourceRepositoryId, string TargetRepositoryName,
    HistoryStrategy Strategy, bool Include = true);

public sealed record PlanCheck(string Code, string Severity, string? RepositoryId = null);

public sealed record PlanRepositoryReview(
    string SourceRepositoryId, string SourceProjectId, string SourceName,
    string TargetRepositoryName, VersionControlKind VersionControl, HistoryStrategy Strategy,
    int RefCount, IReadOnlyList<string> CandidateGitHubLogins);

public sealed record PlanReview(
    Guid SnapshotId, Guid DestinationConnectionId, string TargetOrganization,
    int IdentityDraftRevision, int SelectedRepositories, int AvailableRepositories,
    bool EffectivePermissionsVerified, bool TargetRepositoryAvailabilityVerified,
    bool TargetMembershipVerified, bool ReadyForApproval,
    IReadOnlyList<PlanRepositoryReview> Repositories, IReadOnlyList<PlanCheck> Checks);

public sealed class PlanInputException(string code) : Exception("Invalid migration plan preview")
{
    public string Code { get; } = code;
}

public sealed class MigrationPlanReviewService(
    IDiscoveryRepository discoveries,
    IConnectionRepository connections,
    IIdentityMappingDraftRepository identityDrafts,
    IIdentitySnapshotReader? identitySources = null,
    IPlanRefEvidenceReader? refEvidence = null)
{
    private static readonly Regex RepositoryName = new(
        @"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public async Task<PlanReview> PreviewAsync(Guid snapshotId, Guid destinationId,
        IReadOnlyList<PlanSelectionInput>? selections, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || destinationId == Guid.Empty || selections is null ||
            selections.Count > 1_000)
            throw new PlanInputException("InvalidPlanContext");

        var inventory = await (identitySources is null
            ? discoveries.GetAsync(snapshotId, ct)
            : identitySources.GetMetadataAsync(snapshotId, ct))
            ?? throw new KeyNotFoundException("SnapshotNotFound");
        var destination = await connections.GetAsync(destinationId, ct)
            ?? throw new KeyNotFoundException("DestinationNotFound");
        if (inventory.Id != snapshotId || inventory.Snapshot.SnapshotId != snapshotId)
            throw new PlanInputException("SnapshotMismatch");
        if (destination.Direction != ConnectionDirection.Destination ||
            !string.Equals(destination.ProviderKey, "github-enterprise-cloud", StringComparison.OrdinalIgnoreCase))
            throw new PlanInputException("UnsupportedDestination");
        if (destination.ValidationCode != "Verified")
            throw new PlanInputException("DestinationNotVerified");

        var known = inventory.Snapshot.Projects.SelectMany(p => p.Repositories)
            .ToDictionary(r => r.ProviderId, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<PlanCheck>();
        var output = new List<PlanRepositoryReview>();
        var permissionEvidenceVerified = inventory.PermissionsComplete &&
            inventory.SecurityCoverage.EffectivePermissionsVerified;
        var mapping = await identityDrafts.GetAsync(snapshotId, destinationId, ct);
        if (mapping is not null &&
            !string.Equals(mapping.TargetOrganization, destination.Organization, StringComparison.OrdinalIgnoreCase))
            throw new PlanInputException("IdentityMappingDestinationMismatch");

        if (mapping is not null && identitySources is not null && mapping.Mappings.Count > 0)
        {
            var mappedIds = mapping.Mappings.Select(m => m.SourceIdentityId).ToArray();
            var matched = await identitySources.ResolveKnownAsync(snapshotId, mappedIds, ct);
            if (matched.Count != mappedIds.Distinct(StringComparer.Ordinal).Count())
                throw new PlanInputException("IdentityMappingSourceMismatch");
        }

        // F-014 durable scans store refs in PostgreSQL instead of the inline
        // snapshot array. F-005 must never present an empty array as zero refs.
        // Even historical inline snapshots use DB-side per-repository counts
        // when the production reader is registered.
        IReadOnlyDictionary<string, int>? refCounts = null;
        if (selections.Any(s => s is not null && s.Include))
        {
            if (refEvidence is not null)
                refCounts = await refEvidence.CountByRepositoryAsync(snapshotId, ct);
            else if (inventory.RefsExternalized)
                throw new PlanInputException("PlanRefEvidenceUnavailable");
            if (refCounts is not null && refCounts.Keys.Any(id => !known.ContainsKey(id)))
                throw new PlanInputException("PlanRefEvidenceMismatch");
        }

        foreach (var input in selections)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.SourceRepositoryId) ||
                !seenIds.Add(input.SourceRepositoryId))
                throw new PlanInputException("DuplicateOrMissingSourceRepository");
            if (!known.TryGetValue(input.SourceRepositoryId, out var repo))
                throw new PlanInputException("UnknownSourceRepository");
            if (!Enum.IsDefined(input.Strategy))
                throw new PlanInputException("UnknownHistoryStrategy");
            if (!input.Include) continue;

            var target = input.TargetRepositoryName?.Trim() ?? "";
            if (!RepositoryName.IsMatch(target) || target is "." or ".." ||
                target.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                checks.Add(new("InvalidTargetRepositoryName", "blocker", repo.ProviderId));
            else if (!seenTargets.Add(target))
                checks.Add(new("DuplicateTargetRepositoryName", "blocker", repo.ProviderId));
            if (repo.VersionControl == VersionControlKind.Tfvc && input.Strategy == HistoryStrategy.GitMirror)
                checks.Add(new("TfvcCannotGitMirror", "blocker", repo.ProviderId));
            if (repo.VersionControl == VersionControlKind.Git && input.Strategy != HistoryStrategy.GitMirror)
                checks.Add(new("GitHistoryMayBeReduced", "warning", repo.ProviderId));

            // Show *candidates* only if upstream attests effective permissions. No memberships or
            // GitHub invitations are ever inferred from a draft login.
            IReadOnlyList<string> candidateLogins = [];
            if (permissionEvidenceVerified && mapping is not null)
            {
                var mapped = mapping.Mappings.ToDictionary(m => m.SourceIdentityId,
                    m => m.GitHubLogin, StringComparer.Ordinal);
                candidateLogins = inventory.Snapshot.AccessGrants
                    .Where(a => a.RepositoryId == repo.ProviderId && mapped.ContainsKey(a.SourceIdentityId))
                    .Select(a => mapped[a.SourceIdentityId])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            output.Add(new(repo.ProviderId, repo.ProjectId, repo.Name, target,
                repo.VersionControl, input.Strategy,
                refCounts is null
                    ? inventory.Refs.Count(r => r.RepositoryId == repo.ProviderId)
                    : refCounts.GetValueOrDefault(repo.ProviderId), candidateLogins));
        }

        if (output.Count == 0) checks.Add(new("NoRepositoriesSelected", "blocker"));
        if (!inventory.IdentitiesComplete) checks.Add(new("SourceIdentitiesIncomplete", "blocker"));
        if (!permissionEvidenceVerified) checks.Add(new("EffectivePermissionsUnverified", "blocker"));
        if (mapping is null || mapping.Mappings.Count == 0)
            checks.Add(new("IdentityMappingMissing", "warning"));
        // Destination create permissions, name availability and membership require live provider
        // probes. This slice does not claim they were checked and never sends GitHub writes.
        checks.Add(new("TargetRepositoryAvailabilityUnverified", "blocker"));
        checks.Add(new("TargetMembershipUnverified", "blocker"));

        return new PlanReview(snapshotId, destinationId, destination.Organization,
            mapping?.Revision ?? 0, output.Count, known.Count, permissionEvidenceVerified,
            false, false, false, output, checks);
    }
}
