using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-043: GitHub APP CREDENTIAL SCOPE inspection only. The App private key
// and freshly minted installation access token NEVER enter this model, any
// PostgreSQL record, HTTP response or a Git subprocess. "Issued" attests
// GitHub's token response; it does NOT grant execution or override rulesets.
public sealed record GitHubAppWriteCredentialOptions(
    bool Enabled, long AppId, long InstallationId, string PrivateKeySecretReference,
    string AllowedRepository);

public sealed record GitHubAppWriteScopeObservation(
    long InstallationId, long RepositoryId, string Status,
    DateTimeOffset ObservedAtUtc, DateTimeOffset TokenExpiresAtUtc);

public interface IGitHubAppScopedWriteCredentialInspector
{
    Task<GitHubAppWriteScopeObservation> InspectAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, CancellationToken ct);
}

public sealed record GitHubAppWriteScopeInput(
    Guid PreparationId, Guid PlanId, Guid FenceId,
    Guid WorkpackId, Guid IntentId, Guid RehearsalId,
    Guid DestinationConnectionId,
    string Organization, string Repository,
    string ManifestSha256, string BundleSha256,
    string IntentEvidenceSha256, string RehearsalEvidenceSha256,
    string FenceEvidenceSha256, string RemoteRefFingerprintSha256,
    GitHubAppWriteScopeObservation Observation, Guid ActorId);

public sealed record GitHubAppWriteScopeView(
    Guid Id, Guid PreparationId, Guid PlanId,
    Guid FenceId, Guid WorkpackId, Guid IntentId, Guid RehearsalId,
    string Organization, string Repository, long RepositoryId,
    long InstallationId, string ManifestSha256, string BundleSha256,
    string IntentEvidenceSha256, string RehearsalEvidenceSha256,
    string FenceEvidenceSha256, string RemoteRefFingerprintSha256,
    string EvidenceSha256, string Status,
    DateTimeOffset ObservedAtUtc, DateTimeOffset TokenExpiresAtUtc,
    Guid ActorId, bool IsCurrent, bool ScopedContentsWriteReported,
    bool WriteCapabilityVerified, bool ExecutionAuthorized,
    bool RemoteWritesPerformed, bool ReadyForExecution);

public sealed class GitHubAppWriteScopeException(string code)
    : Exception("GitHub App write scope attestation rejected")
{
    public string Code { get; } = code;
}

public static class GitHubAppWriteScopeIntegrity
{
    public const string Issued = "ScopedContentsWriteIssued";

    public static string Seal(GitHubAppWriteScopeInput input)
    {
        if (input.PreparationId == Guid.Empty || input.PlanId == Guid.Empty ||
            input.FenceId == Guid.Empty || input.WorkpackId == Guid.Empty ||
            input.IntentId == Guid.Empty || input.RehearsalId == Guid.Empty ||
            input.DestinationConnectionId == Guid.Empty || input.ActorId == Guid.Empty ||
            !GitHubRefObservationIntegrity.ValidOwner(input.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(input.Repository) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.BundleSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.IntentEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.RehearsalEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.FenceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.RemoteRefFingerprintSha256) ||
            input.Observation.Status != Issued ||
            input.Observation.InstallationId <= 0 ||
            input.Observation.RepositoryId <= 0 ||
            input.Observation.ObservedAtUtc == default ||
            input.Observation.TokenExpiresAtUtc <= input.Observation.ObservedAtUtc.AddMinutes(2) ||
            input.Observation.TokenExpiresAtUtc >
                input.Observation.ObservedAtUtc.AddMinutes(65))
            throw new GitHubAppWriteScopeException("AppScopeEvidenceInvalid");
        var stable = JsonSerializer.Serialize(new object?[] {
            "github-app-write-scope-review-v1",
            input.PreparationId.ToString("D"), input.PlanId.ToString("D"),
            input.FenceId.ToString("D"), input.WorkpackId.ToString("D"),
            input.IntentId.ToString("D"), input.RehearsalId.ToString("D"),
            input.DestinationConnectionId.ToString("D"),
            input.Organization.ToLowerInvariant(),
            input.Repository.ToLowerInvariant(),
            input.ManifestSha256, input.BundleSha256,
            input.IntentEvidenceSha256, input.RehearsalEvidenceSha256,
            input.FenceEvidenceSha256, input.RemoteRefFingerprintSha256,
            input.Observation.InstallationId, input.Observation.RepositoryId,
            input.Observation.Status,
            input.Observation.ObservedAtUtc.ToUnixTimeMilliseconds(),
            input.Observation.TokenExpiresAtUtc.ToUnixTimeMilliseconds(),
            input.ActorId.ToString("D")
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
    }
}

public interface IGitHubAppWriteScopeStore
{
    Task<GitHubAppWriteScopeView> SaveAsync(
        GitHubAppWriteScopeInput input, CancellationToken ct);
    Task<GitHubAppWriteScopeView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct);
    Task<IReadOnlyList<GitHubAppWriteScopeView>> ListAsync(
        Guid preparationId, Guid planId, Guid fenceId, CancellationToken ct);
}

public sealed class GitHubAppWriteScopeService(
    GitHubAppWriteCredentialOptions options,
    IGitHubAppScopedWriteCredentialInspector inspector,
    IGitHubAppWriteScopeStore store,
    IGitHubRemoteRefFenceStore fences,
    IGitHubImportIntentStore intents,
    IGitHubImportReadinessStore readiness,
    IIncrementalOperationPlanRepository plans,
    IConnectionRepository connections,
    IConnectionSecretStore secrets)
{
    public async Task<GitHubAppWriteScopeView> InspectAsync(
        Guid preparationId, Guid planId, Guid fenceId,
        string expectedFenceEvidenceSha256, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            fenceId == Guid.Empty || actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedFenceEvidenceSha256))
            throw new GitHubAppWriteScopeException("AppScopeRequestInvalid");

        // Explicit operator opt-in and one repository, not a user-supplied
        // destination URL. Never fallback to the read-only connection PAT.
        if (!options.Enabled || options.AppId <= 0 ||
            options.InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(options.PrivateKeySecretReference) ||
            string.IsNullOrWhiteSpace(options.AllowedRepository) ||
            options.AllowedRepository.Count(c => c == '/') != 1)
            throw new GitHubAppWriteScopeException("AppScopeDisabledOrUnconfigured");

        var fence = await fences.GetAsync(preparationId, planId, fenceId, ct)
            ?? throw new KeyNotFoundException("RemoteFenceNotFound");
        if (!fence.IsCurrent || fence.Status != "RefSetUnchanged" ||
            fence.EvidenceSha256 != expectedFenceEvidenceSha256 ||
            fence.WriteCapabilityVerified || fence.ExecutionAuthorized ||
            fence.RemoteWritesPerformed || fence.ReadyForExecution)
            throw new GitHubAppWriteScopeException("AppScopeFenceNotCurrent");

        var intent = await intents.GetAsync(
            preparationId, planId, fence.IntentId, ct)
            ?? throw new KeyNotFoundException("ImportIntentNotFound");
        if (!intent.IsCurrent || intent.IsExpired ||
            intent.State != GitHubImportIntentStates.Acknowledged ||
            intent.EvidenceSha256 != fence.IntentEvidenceSha256 ||
            intent.ReviewedBy != actorId || intent.RequestedBy == actorId)
            throw new GitHubAppWriteScopeException("AppScopeReviewerNotAuthorized");

        var review = await readiness.GetAsync(
            preparationId, planId, intent.ImportReadinessId, ct)
            ?? throw new KeyNotFoundException("ImportReadinessNotFound");
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || !review.IsCurrent ||
            plan.ManifestSha256 != fence.ManifestSha256 ||
            review.ManifestSha256 != fence.ManifestSha256 ||
            review.SourceBundleSha256 != fence.BundleSha256 ||
            review.TargetRepositoryName.Length == 0 ||
            !string.Equals(options.AllowedRepository,
                review.Organization + "/" + review.TargetRepositoryName,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubAppWriteScopeException("AppScopeRepositoryNotAllowlisted");
        var destination = await connections.GetAsync(plan.DestinationConnectionId, ct)
            ?? throw new GitHubAppWriteScopeException("AppScopeDestinationMissing");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, review.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination.SecretReference,
                options.PrivateKeySecretReference, StringComparison.Ordinal))
            throw new GitHubAppWriteScopeException("AppScopeCredentialSeparationFailed");

        // The App signing key is an independent, privileged Key Vault
        // reference. It is neither returned nor persisted. A minted token
        // is consumed *inside* the inspector, not returned to this service.
        var pem = await secrets.ReadAsync(options.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 32_768)
            throw new GitHubAppWriteScopeException("AppScopeSigningKeyMissing");
        GitHubAppWriteScopeObservation observation;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                observation = await inspector.InspectAsync(
                    options.AppId, options.InstallationId, pem,
                    review.Organization, review.TargetRepositoryName, timeout.Token);
            }
            catch (HttpRequestException)
            { throw new GitHubAppWriteScopeException("AppScopeGitHubUnavailable"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new GitHubAppWriteScopeException("AppScopeGitHubTimeout"); }
        }
        var input = new GitHubAppWriteScopeInput(
            preparationId, planId, fenceId, fence.WorkpackId,
            fence.IntentId, fence.RehearsalId, plan.DestinationConnectionId,
            review.Organization, review.TargetRepositoryName,
            plan.ManifestSha256, fence.BundleSha256,
            intent.EvidenceSha256, fence.RehearsalEvidenceSha256,
            fence.EvidenceSha256, fence.RemoteRefFingerprintSha256,
            observation, actorId);
        _ = GitHubAppWriteScopeIntegrity.Seal(input);
        return await store.SaveAsync(input, ct);
    }

    public Task<GitHubAppWriteScopeView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct) =>
        store.GetAsync(preparationId, planId, scopeId, ct);

    public Task<IReadOnlyList<GitHubAppWriteScopeView>> ListAsync(
        Guid preparationId, Guid planId, Guid fenceId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, fenceId, ct);
}
