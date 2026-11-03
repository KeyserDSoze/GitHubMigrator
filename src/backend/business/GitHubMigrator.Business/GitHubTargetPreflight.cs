using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// A GitHub 404 is ambiguous (missing or hidden). Only 200 with matching full_name
// establishes a visible collision. Nothing here verifies repo creation privileges.
public interface IGitHubTargetRepositoryProbe
{
    Task<string> ProbeAsync(string organization, string repository, string token, CancellationToken ct);
}

public sealed record TargetRepositoryProbeResult(
    string SourceRepositoryId, string TargetRepositoryName, string Status);

public sealed record MigrationTargetPreflight(
    Guid SnapshotId, Guid DestinationConnectionId, string TargetOrganization,
    DateTimeOffset CheckedAtUtc, int CheckedCount, bool TargetAvailabilityVerified,
    bool ReadyForApproval, IReadOnlyList<TargetRepositoryProbeResult> Repositories);

public sealed class TargetPreflightInputException(string code) : Exception("Target preflight request rejected")
{
    public string Code { get; } = code;
}

public sealed class GitHubTargetPreflightService(
    MigrationPlanReviewService reviews,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitHubTargetRepositoryProbe probe,
    TimeProvider clock)
{
    public async Task<MigrationTargetPreflight> CheckAsync(Guid snapshotId, Guid destinationId,
        IReadOnlyList<PlanSelectionInput>? selections, CancellationToken ct)
    {
        // Preview validates snapshot/destination binding, provider kind, repo scope and IDs.
        var preview = await reviews.PreviewAsync(snapshotId, destinationId, selections, ct);
        if (preview.SelectedRepositories is < 1 or > 10)
            throw new TargetPreflightInputException("PreflightSelectionLimit");
        if (preview.Checks.Any(x => x.Code is "InvalidTargetRepositoryName" or "DuplicateTargetRepositoryName"))
            throw new TargetPreflightInputException("InvalidPreflightTargetNames");

        var destination = await connections.GetAsync(destinationId, ct)
            ?? throw new KeyNotFoundException("DestinationNotFound");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new TargetPreflightInputException("DestinationCredentialMissing");

        var checks = new List<TargetRepositoryProbeResult>();
        foreach (var repository in preview.Repositories)
        {
            string status;
            try
            {
                status = await probe.ProbeAsync(destination.Organization,
                    repository.TargetRepositoryName, token, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                status = "Unavailable";
            }
            catch (HttpRequestException)
            {
                status = "Unavailable";
            }
            checks.Add(new(repository.SourceRepositoryId, repository.TargetRepositoryName, status));
        }

        // Even all 404 responses do not establish name availability: private or hidden repositories
        // may return 404, and only GitHub repository creation can ultimately decide conflicts.
        return new(snapshotId, destinationId, destination.Organization, clock.GetUtcNow(),
            checks.Count, false, false, checks);
    }
}
