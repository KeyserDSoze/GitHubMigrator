using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-004 is a reviewable identity *draft*. No ACL or GitHub membership is inferred or modified.
public sealed record IdentityMappingDraft(
    Guid SnapshotId, Guid DestinationConnectionId, string TargetOrganization,
    int Revision, DateTimeOffset UpdatedAtUtc, IReadOnlyList<IdentityMapping> Mappings);

public sealed record IdentityMappingInput(string SourceIdentityId, string GitHubLogin);
public sealed record IdentityMappingIssue(string Code, string? SourceIdentityId = null);
public sealed record IdentityMappingPreview(
    int DiscoveredIdentities, int MappedIdentities, int UnmappedIdentities,
    int UnresolvedAclPrincipals, bool SourceIdentitiesComplete, bool EffectivePermissionsVerified,
    bool TargetAccountsVerified, bool ReadyForGrants,
    IReadOnlyList<IdentityMappingIssue> Issues);

public sealed record IdentityMappingReview(IdentityMappingDraft Draft, IdentityMappingPreview Preview);

public sealed class MappingInputException(string code) : Exception("Invalid identity mapping request")
{
    public string Code { get; } = code;
}

public sealed class MappingRevisionConflictException : Exception("Identity mapping draft was changed concurrently");

public interface IIdentityMappingDraftRepository
{
    Task<IdentityMappingDraft?> GetAsync(Guid snapshotId, Guid destinationConnectionId, CancellationToken ct);
    // Must use an atomic compare-and-swap. expectedRevision=0 means insert-only.
    Task<bool> SaveAsync(IdentityMappingDraft draft, int expectedRevision, CancellationToken ct);
}

public sealed class IdentityMappingService(
    IDiscoveryRepository discoveries,
    IConnectionRepository connections,
    IIdentityMappingDraftRepository drafts,
    TimeProvider clock)
{
    private static readonly Regex GitHubLoginPattern = new(
        @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<IdentityMappingReview> GetAsync(Guid snapshotId, Guid destinationId, CancellationToken ct)
    {
        var (inventory, destination) = await LoadContextAsync(snapshotId, destinationId, ct);
        var draft = await drafts.GetAsync(snapshotId, destinationId, ct)
            ?? new IdentityMappingDraft(snapshotId, destinationId, destination.Organization,
                0, clock.GetUtcNow(), []);
        return new(draft, Preview(inventory, draft));
    }

    public async Task<IdentityMappingReview> SaveAsync(Guid snapshotId, Guid destinationId,
        int expectedRevision, IReadOnlyList<IdentityMappingInput>? inputs, CancellationToken ct)
    {
        if (expectedRevision is < 0 or int.MaxValue || inputs is null || inputs.Count > 10_000)
            throw new MappingInputException("InvalidMappingBatch");

        var (inventory, destination) = await LoadContextAsync(snapshotId, destinationId, ct);
        var known = inventory.Snapshot.Identities.Select(i => i.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        var uniqueSource = new HashSet<string>(StringComparer.Ordinal);
        var uniqueLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<IdentityMapping>();
        foreach (var input in inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.SourceIdentityId) ||
                !known.Contains(input.SourceIdentityId))
                throw new MappingInputException("UnknownSourceIdentity");
            if (!uniqueSource.Add(input.SourceIdentityId))
                throw new MappingInputException("DuplicateSourceIdentity");
            var login = input.GitHubLogin?.Trim();
            if (string.IsNullOrWhiteSpace(login) || !GitHubLoginPattern.IsMatch(login) ||
                login.Contains("--", StringComparison.Ordinal))
                throw new MappingInputException("InvalidGitHubLogin");
            if (!uniqueLogins.Add(login))
                throw new MappingInputException("DuplicateGitHubLogin");
            mappings.Add(new(input.SourceIdentityId, login));
        }
        var next = new IdentityMappingDraft(snapshotId, destinationId, destination.Organization,
            expectedRevision + 1, clock.GetUtcNow(),
            mappings.OrderBy(m => m.SourceIdentityId, StringComparer.Ordinal).ToArray());
        if (!await drafts.SaveAsync(next, expectedRevision, ct))
            throw new MappingRevisionConflictException();
        return new(next, Preview(inventory, next));
    }

    private async Task<(DiscoveryInventory inventory, MigrationConnection destination)> LoadContextAsync(
        Guid snapshotId, Guid destinationId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || destinationId == Guid.Empty)
            throw new MappingInputException("InvalidMappingContext");
        var inventory = await discoveries.GetAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("DiscoverySnapshotNotFound");
        var destination = await connections.GetAsync(destinationId, ct)
            ?? throw new KeyNotFoundException("DestinationConnectionNotFound");
        if (destination.Direction != ConnectionDirection.Destination ||
            !string.Equals(destination.ProviderKey, "github-enterprise-cloud", StringComparison.OrdinalIgnoreCase))
            throw new MappingInputException("NotGitHubDestination");
        if (destination.ValidationCode != "Verified")
            throw new MappingInputException("DestinationNotVerified");
        return (inventory, destination);
    }

    public static IdentityMappingPreview Preview(DiscoveryInventory inventory, IdentityMappingDraft draft)
    {
        if (inventory.Id != draft.SnapshotId)
            throw new ArgumentException("Draft is linked to a different discovery snapshot.");
        var discovered = inventory.Snapshot.Identities;
        var mapped = draft.Mappings.Select(m => m.SourceIdentityId).ToHashSet(StringComparer.Ordinal);
        var missing = discovered.Where(i => !mapped.Contains(i.ProviderId)).ToArray();
        var issues = new List<IdentityMappingIssue>();
        if (!inventory.IdentitiesComplete) issues.Add(new("SourceIdentitiesIncomplete"));
        if (!inventory.PermissionsComplete || !inventory.SecurityCoverage.EffectivePermissionsVerified)
            issues.Add(new("EffectivePermissionsUnverified"));
        issues.Add(new("TargetMembershipUnverified"));
        // A missing mapping is not proof of orphan access. It is a review item for a visible identity.
        issues.AddRange(missing.Take(100).Select(i => new IdentityMappingIssue("UnmappedSourceIdentity", i.ProviderId)));

        // An ACE descriptor not present among Graph users/groups is preserved as unresolved evidence.
        var resolved = discovered.Select(i => i.LegacyDescriptor)
            .Concat(inventory.Groups.Select(g => g.LegacyDescriptor))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
        var unknown = inventory.AclEvidence.Select(x => x.SecurityDescriptor)
            .Where(x => !resolved.Contains(x)).Distinct(StringComparer.Ordinal).ToArray();
        issues.AddRange(unknown.Take(25).Select(_ => new IdentityMappingIssue("AclPrincipalUnresolved")));

        return new IdentityMappingPreview(
            discovered.Count, mapped.Count, missing.Length, unknown.Length,
            inventory.IdentitiesComplete,
            inventory.PermissionsComplete && inventory.SecurityCoverage.EffectivePermissionsVerified,
            false, false, issues);
    }
}
