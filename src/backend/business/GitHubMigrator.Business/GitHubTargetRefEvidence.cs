using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-031 stores immutable read-only GitHub ref evidence for exactly one
// repository mapping of a current, verified F-029 operation manifest.
// No migration worker, permission authorization or outbound write adapter.
public sealed record GitHubTargetRefEvidenceView(
    Guid Id, Guid PlanId, string SourceRepositoryId, string TargetRepositoryName,
    string Organization, string ManifestSha256, string EvidenceSha256,
    DateTimeOffset ObservedAtUtc, int RefCount, bool IsCurrent,
    bool ReadyForExecution);

public sealed record GitHubTargetRefEvidencePage(
    GitHubTargetRefEvidenceView Evidence,
    IReadOnlyList<GitHubObservedRef> Items, string? NextAfter);

public sealed class GitHubTargetRefEvidenceException(string code)
    : Exception("Immutable GitHub ref evidence rejected")
{
    public string Code { get; } = code;
}

public interface IGitHubTargetRefEvidenceStore
{
    Task<GitHubTargetRefEvidenceView> SaveAsync(
        IncrementalOperationPlan plan, string sourceRepositoryId,
        Guid actorId, DateTimeOffset? destinationValidatedAtUtc,
        GitHubRefObservation observation, CancellationToken ct);
    Task<IReadOnlyList<GitHubTargetRefEvidenceView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
    Task<GitHubTargetRefEvidencePage?> PageAsync(
        Guid preparationId, Guid planId, Guid observationId,
        string? after, int take, CancellationToken ct);
}

public sealed class GitHubTargetRefEvidenceService(
    IncrementalOperationPlanService plans,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitHubRefObservationReader reader,
    IGitHubTargetRefEvidenceStore store)
{
    public async Task<GitHubTargetRefEvidenceView> CollectAsync(
        Guid preparationId, Guid planId, string sourceRepositoryId,
        Guid actorId, string expectedManifestSha256, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            sourceRepositoryId.Length > 256 ||
            expectedManifestSha256 is not { Length: 64 } ||
            expectedManifestSha256.Any(c => c is not (>= '0' and <= '9'
                or >= 'a' and <= 'f')))
            throw new GitHubTargetRefEvidenceException("InvalidRefEvidenceRequest");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.ManifestSha256 != expectedManifestSha256)
            throw new GitHubTargetRefEvidenceException("RefEvidencePlanNotCurrent");

        var mapping = plan.Steps.SingleOrDefault(x =>
            x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == sourceRepositoryId);
        if (mapping is null)
            throw new GitHubTargetRefEvidenceException("RefEvidenceMappingInvalid");

        var destination = await connections.GetAsync(plan.DestinationConnectionId, ct)
            ?? throw new GitHubTargetRefEvidenceException("DestinationUnavailable");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubTargetRefEvidenceException("DestinationValidationChanged");

        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubTargetRefEvidenceException("DestinationCredentialMissing");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var observed = await reader.ReadAsync(destination.Organization,
            mapping.TargetRepositoryName, token, timeout.Token);
        // A forged reader cannot swap repositories or silently alter its
        // proof; the persistence boundary re-verifies all fields as well.
        if (!string.Equals(observed.Organization, destination.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observed.Repository, mapping.TargetRepositoryName,
                StringComparison.OrdinalIgnoreCase) ||
            GitHubRefObservationIntegrity.Seal(
                observed.Organization, observed.Repository, observed.ObservedAtUtc,
                observed.Refs).EvidenceSha256 != observed.EvidenceSha256)
            throw new GitHubTargetRefEvidenceException("RefEvidenceProviderMismatch");

        // DB rechecks F-029 freshness, mapping and validated-at timestamp
        // inside a source-scoped advisory lock *after* the external GETs.
        return await store.SaveAsync(plan, sourceRepositoryId, actorId,
            destination.ValidatedAtUtc, observed, ct);
    }

    public Task<IReadOnlyList<GitHubTargetRefEvidenceView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, ct);

    public Task<GitHubTargetRefEvidencePage?> PageAsync(
        Guid preparationId, Guid planId, Guid observationId,
        string? after, int take, CancellationToken ct)
    {
        if (take is < 1 or > 100 || (after?.Length ?? 0) > 255)
            throw new GitHubTargetRefEvidenceException("InvalidRefEvidencePage");
        return store.PageAsync(preparationId, planId, observationId,
            after, take, ct);
    }
}
