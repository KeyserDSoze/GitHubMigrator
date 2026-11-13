using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-036: fetches ONLY from a server-derived Azure DevOps Git URL (Services, or an allow-listed
// Azure DevOps Server). No caller-provided URI, refspec, credential or filesystem path is accepted.
// For Server, Organization is the collection and ServerRoot the connection's validated server root.
public sealed record AzureGitSourceLocation(
    Guid ConnectionId, string Organization, string ProjectName,
    string RepositoryName, string RepositoryId,
    string SourceProvider = ProviderKeys.AzureDevOpsServices, string? ServerRoot = null);

public sealed record AzureGitMirrorFetchRequest(
    AzureGitSourceLocation Location, string PersonalAccessToken,
    IReadOnlyList<GitMirrorRef> ExpectedRefs);

public interface IAzureGitSourceLocationResolver
{
    Task<AzureGitSourceLocation> ResolveAsync(
        Guid snapshotId, Guid connectionId, string repositoryId,
        CancellationToken ct);
}

public interface IAzureGitSourceAcquisition
{
    Task FetchMirrorAsync(Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct);
    Task<GitSourceBundleView> BundleVerifiedMirrorAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct);
}

public interface IAzureGitMirrorFetcher
{
    Task FetchAsync(AzureGitMirrorFetchRequest request, CancellationToken ct);
}

// The location comes from the immutable snapshot; the connection decides whether it may be fetched and
// (for Azure DevOps Server) where. Nothing here is caller supplied.
public static class AzureGitSourceBinding
{
    public static AzureGitSourceLocation Bind(AzureGitSourceLocation location, MigrationConnection connection)
    {
        if (!ProviderKeys.IsAzureDevOpsSource(connection.ProviderKey) ||
            connection.ProviderKey != location.SourceProvider ||
            connection.Direction != ConnectionDirection.Source ||
            connection.ValidationCode != "Verified" ||
            connection.Organization != location.Organization ||
            location.ConnectionId != connection.Id)
            throw new GitSourceBundleException("AzureGitSourceNotVerified");
        // The server root comes from the verified connection, never from the caller; the fetcher
        // re-validates it against the egress policy right before any network access.
        if (!ProviderKeys.IsServer(connection.ProviderKey)) return location with { ServerRoot = null };
        if (string.IsNullOrWhiteSpace(connection.BaseUrl))
            throw new GitSourceBundleException("AzureGitSourceNotVerified");
        return location with { ServerRoot = connection.BaseUrl };
    }
}

// Fetch and local F-035 bundle export are separately bounded: a remote read
// NEVER carries the capability to write to the GitHub destination. The F-035
// immutable PostgreSQL artifact remains the committed checkpoint.
public sealed class AzureGitSourceAcquisitionService(
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IAzureGitSourceLocationResolver locations,
    IAzureGitMirrorFetcher fetcher,
    IGitTransferWorkpackRepository workpacks,
    IIncrementalOperationPlanRepository plans,
    IIncrementalGitRefReconciliationStore reconciliations,
    IVerifiedSourceGitRefReader refs,
    GitSourceBundleService bundles)
    : IAzureGitSourceAcquisition
{
    public async Task<GitSourceBundleView> FetchAndBundleAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct)
    {
        var existing = await GetExistingAsync(
            preparationId, planId, workpackId, expectedReconciliationSha256, ct);
        if (existing is not null) return existing;
        await FetchMirrorAsync(preparationId, planId, workpackId,
            expectedReconciliationSha256, ct);
        return await BundleVerifiedMirrorAsync(
            preparationId, planId, workpackId, expectedReconciliationSha256, ct);
    }

    // F-037: durable worker checkpoints ONLY after the full mirror is
    // re-verified; an interrupted fetch leaves no successful DB event.
    public async Task FetchMirrorAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            workpackId == Guid.Empty || !ValidSha(expectedReconciliationSha256))
            throw new GitSourceBundleException("InvalidBundleRequest");

        var pack = await workpacks.GetAsync(
            preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!pack.IsCurrent || pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            pack.ReconciliationSha256 != expectedReconciliationSha256)
            throw new GitSourceBundleException("BundleWorkpackNotCurrent");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.SourceConnectionId == Guid.Empty ||
            plan.ManifestSha256 != pack.ManifestSha256)
            throw new GitSourceBundleException("BundlePlanNotCurrent");

        var comparison = await reconciliations.PageAsync(
            preparationId, planId, pack.ReconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        if (!comparison.Reconciliation.IsCurrent ||
            comparison.Reconciliation.ReconciliationSha256 != pack.ReconciliationSha256 ||
            comparison.Reconciliation.SourceRepositoryId != pack.SourceRepositoryId ||
            comparison.Reconciliation.TargetEvidenceSha256 != pack.TargetEvidenceSha256)
            throw new GitSourceBundleException("BundleReconciliationNotCurrent");

        var source = await refs.ReadAsync(plan, pack.SourceRepositoryId, ct);
        if (source.SnapshotId != plan.SnapshotId ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.SourceEvidenceSha256 != comparison.Reconciliation.SourceEvidenceSha256)
            throw new GitSourceBundleException("BundleSourceEvidenceMismatch");

        // A previously finalized bundle makes the remote fetch unnecessary.
        // The queued worker will still re-verify its DB proof.
        var prior = await bundles.GetAsync(preparationId, planId, workpackId, ct);
        if (prior is not null)
        {
            if (!prior.IsCurrent ||
                prior.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
                prior.ReconciliationSha256 != expectedReconciliationSha256)
                throw new GitSourceBundleException("BundleProofMismatch");
            await bundles.VerifyStoredAsync(preparationId, planId, workpackId, ct);
            return;
        }

        var location = await locations.ResolveAsync(plan.SnapshotId,
            plan.SourceConnectionId, pack.SourceRepositoryId, ct);
        var connection = await connections.GetAsync(plan.SourceConnectionId, ct)
            ?? throw new KeyNotFoundException("SourceConnectionNotFound");
        location = AzureGitSourceBinding.Bind(location, connection);
        var token = await secrets.ReadAsync(connection.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
            token.Any(char.IsControl))
            throw new GitSourceBundleException("AzureGitCredentialUnavailable");

        // Only the read-only Git subprocess gets the PAT, inside its
        // environment. Never persist it or include it in exception messages.
        await fetcher.FetchAsync(new(location, token, source.Refs), ct);

        // The F-036 adapter already atomically promotes only exact-ref and
        // fsck-validated mirrors; F-037 records a separate leased checkpoint.
    }

    public Task<GitSourceBundleView> BundleVerifiedMirrorAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct) =>
        bundles.AcquireAsync(preparationId, planId, workpackId,
            expectedReconciliationSha256, ct);

    public async Task<GitSourceBundleView?> GetExistingAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct)
    {
        if (!ValidSha(expectedReconciliationSha256))
            throw new GitSourceBundleException("InvalidBundleRequest");
        var prior = await bundles.GetAsync(preparationId, planId, workpackId, ct);
        if (prior is not null)
        {
            if (!prior.IsCurrent ||
                prior.ReconciliationSha256 != expectedReconciliationSha256)
                throw new GitSourceBundleException("BundleProofMismatch");
            await bundles.VerifyStoredAsync(preparationId, planId, workpackId, ct);
        }
        return prior;
    }

    private static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
