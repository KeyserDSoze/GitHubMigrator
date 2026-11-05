using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-028 reports only independently observed read-only GitHub evidence.
// GitHub 404 may mean a hidden repository; GET cannot prove creation rights.
// A preflight never constitutes an execution permit, even after planning review.
public sealed record IncrementalTargetProbe(
    string SourceRepositoryId, string TargetRepositoryName,
    string HistoryStrategy, string Status);

public sealed record IncrementalTargetPreflightView(
    Guid PreparationId, Guid SnapshotId, Guid DestinationConnectionId,
    string Organization, string ReviewSha256, string EvidenceSha256,
    DateTimeOffset CheckedAtUtc, int Revision, string State,
    string MembershipStatus, string MembershipRole, string PrivateRepositoryPolicy,
    int CheckedRepositories, int VisibleCollisions, int UncertainRepositories,
    bool MembershipVerified, bool RepositoryCreatePermissionVerified,
    bool TargetAvailabilityVerified, bool ReadyForExecution,
    bool IsCurrent, IReadOnlyList<IncrementalTargetProbe> Repositories);

public sealed record IncrementalTargetPreflightEvent(
    long Id, Guid PreparationId, Guid ActorId, DateTimeOffset CheckedAtUtc,
    string EvidenceSha256, string State, int Revision);

public sealed record IncrementalTargetPreflightEvidence(
    Guid PreparationId, Guid SnapshotId, Guid SourceConnectionId,
    Guid DestinationConnectionId, Guid ActorId, string Organization,
    string ReviewSha256, DateTimeOffset? SourceValidatedAtUtc,
    DateTimeOffset? DestinationValidatedAtUtc, DateTimeOffset CheckedAtUtc,
    string MembershipStatus, string MembershipRole, string PrivateRepositoryPolicy,
    IReadOnlyList<IncrementalTargetProbe> Repositories)
{
    // Deliberately no status allowing destination execution. All 404s still
    // mean unknown/possibly hidden repositories, not approved availability.
    public string State =>
        MembershipStatus != "Active" ||
        Repositories.Any(r => r.Status is "Exists" or "Unauthorized" or "Forbidden")
            ? "Blocked" : "Unverified";

    public string EvidenceSha256
    {
        get
        {
            var payload = JsonSerializer.Serialize(new object?[] {
                "incremental-target-preflight-v1", PreparationId.ToString("D"),
                SnapshotId.ToString("D"), SourceConnectionId.ToString("D"),
                DestinationConnectionId.ToString("D"), Organization,
                ReviewSha256, SourceValidatedAtUtc?.ToUniversalTime().ToString("O"),
                DestinationValidatedAtUtc?.ToUniversalTime().ToString("O"),
                CheckedAtUtc.ToUniversalTime().ToString("O"),
                State, MembershipStatus, MembershipRole, PrivateRepositoryPolicy,
                Repositories.OrderBy(r => r.SourceRepositoryId, StringComparer.Ordinal)
                    .Select(r => new[] { r.SourceRepositoryId, r.TargetRepositoryName,
                        r.HistoryStrategy, r.Status }).ToArray()
            });
            return Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(payload)));
        }
    }
}

public sealed class IncrementalTargetPreflightException(string code)
    : Exception("Live destination preflight rejected")
{
    public string Code { get; } = code;
}

public interface IIncrementalTargetPreflightRepository
{
    Task<IncrementalTargetPreflightView> SaveAsync(
        IncrementalTargetPreflightEvidence evidence, CancellationToken ct);
    Task<IncrementalTargetPreflightView?> GetAsync(
        Guid preparationId, CancellationToken ct);
    Task<IReadOnlyList<IncrementalTargetPreflightEvent>> EventsAsync(
        Guid preparationId, CancellationToken ct);
}

public sealed class IncrementalTargetPreflightService(
    IIncrementalPreparationRepository preparations,
    IIncrementalReviewRepository reviews,
    MigrationPlanReviewService planPreview,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    GitHubDestinationAccessService access,
    IGitHubTargetRepositoryProbe repositoryProbe,
    IIncrementalTargetPreflightRepository store,
    TimeProvider clock)
{
    private static readonly HashSet<string> ValidProbeStatuses =
        new(StringComparer.Ordinal)
        {
            "Exists", "NotVisibleOrMissing", "Unauthorized", "Forbidden",
            "RateLimited", "InvalidResponse", "Unavailable"
        };

    public async Task<IncrementalTargetPreflightView> CheckAsync(
        Guid preparationId, Guid actorId, string expectedReviewSha256,
        IReadOnlyList<PlanSelectionInput>? repositories, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            expectedReviewSha256 is not { Length: 64 } ||
            repositories is null || repositories.Count is < 1 or > 10 ||
            repositories.Any(r => r is null || !r.Include ||
                (r.SourceRepositoryId?.Length ?? 0) > 256 ||
                (r.TargetRepositoryName?.Length ?? 0) > 128))
            throw new IncrementalTargetPreflightException("InvalidTargetPreflightRequest");

        var run = await preparations.GetAsync(preparationId, ct)
            ?? throw new KeyNotFoundException("PreparationNotFound");
        var review = await reviews.GetAsync(preparationId, ct)
            ?? throw new IncrementalTargetPreflightException("PlanningReviewRequired");
        if (run.State != IncrementalPreparationStates.Prepared ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            !review.IsCurrent ||
            review.DryRunSha256 != expectedReviewSha256 ||
            review.SnapshotId != run.SnapshotId ||
            review.DestinationConnectionId != run.DestinationConnectionId ||
            review.EvidenceSha256 != run.EvidenceSha256)
            throw new IncrementalTargetPreflightException("PlanningReviewNotCurrent");

        // Reuse the F-005 source project/repository and target-name validator.
        // Permission/blocking findings remain unverified even if GET succeeds.
        var preview = await planPreview.PreviewAsync(run.SnapshotId,
            run.DestinationConnectionId, repositories, ct);
        if (preview.SelectedRepositories != repositories.Count ||
            preview.Checks.Any(c => c.Code is
                "InvalidTargetRepositoryName" or "DuplicateTargetRepositoryName" or
                "TfvcCannotGitMirror" or "NoRepositoriesSelected"))
            throw new IncrementalTargetPreflightException("InvalidTargetRepositorySelection");

        var destination = await connections.GetAsync(run.DestinationConnectionId, ct)
            ?? throw new IncrementalTargetPreflightException("DestinationUnavailable");
        var source = await connections.GetAsync(run.SourceConnectionId, ct)
            ?? throw new IncrementalTargetPreflightException("SourceUnavailable");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            source.Direction != ConnectionDirection.Source ||
            source.ProviderKey != "azure-devops-services" ||
            source.ValidationCode != "Verified" ||
            !string.Equals(preview.TargetOrganization, destination.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new IncrementalTargetPreflightException("ConnectionValidationChanged");

        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new IncrementalTargetPreflightException("DestinationCredentialMissing");

        DestinationAccessReview accessReview;
        try { accessReview = await access.CheckAsync(destination.Id, ct); }
        catch (DestinationAccessInputException)
        {
            throw new IncrementalTargetPreflightException("DestinationAccessUnavailable");
        }
        // F-005 adapter classifies 404 as "NotVisibleOrMissing" rather than
        // proof that a new repository name is free. Limit probes to 10.
        var observations = new List<IncrementalTargetProbe>(preview.Repositories.Count);
        foreach (var repository in preview.Repositories.OrderBy(x => x.SourceRepositoryId,
                     StringComparer.Ordinal))
        {
            string status;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                status = await repositoryProbe.ProbeAsync(destination.Organization,
                    repository.TargetRepositoryName, token, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                status = "Unavailable";
            }
            catch (HttpRequestException)
            {
                status = "Unavailable";
            }
            if (!ValidProbeStatuses.Contains(status))
                status = "Unavailable";
            observations.Add(new(repository.SourceRepositoryId,
                repository.TargetRepositoryName, repository.Strategy.ToString(), status));
        }

        var evidence = new IncrementalTargetPreflightEvidence(
            run.Id, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, actorId, destination.Organization,
            review.DryRunSha256, source.ValidatedAtUtc, destination.ValidatedAtUtc,
            clock.GetUtcNow(), accessReview.MembershipStatus,
            accessReview.MembershipRole, accessReview.PrivateRepositoryPolicy,
            observations);
        // The database must recheck the approved review and source/connection
        // versions AFTER the external HTTP requests to prevent TOCTOU saves.
        return await store.SaveAsync(evidence, ct);
    }

    public Task<IncrementalTargetPreflightView?> GetAsync(
        Guid preparationId, CancellationToken ct) =>
        store.GetAsync(preparationId, ct);

    public Task<IReadOnlyList<IncrementalTargetPreflightEvent>> EventsAsync(
        Guid preparationId, CancellationToken ct) =>
        store.EventsAsync(preparationId, ct);
}
