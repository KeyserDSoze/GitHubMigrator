using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-059 accepts ONLY handles, historical refs (to verify against database
// fingerprints), and an ephemeral token. The source snapshot and its refs
// are selected by a trusted persisted F-029/F-014 plan reader, never
// chosen by the external caller. This is not an API or a background sender.
public sealed record GitHubTrustedRemoteReadOptions(bool Enabled);

public sealed record GitHubTrustedRemoteReadCommand(
    Guid GrantId, Guid ReadbackId, Guid HazardId, long RepositoryId,
    Guid ObserverId, IReadOnlyList<GitHubObservedRef> OriginalBaseline,
    IReadOnlyList<GitMirrorRef> CreateCandidates,
    DateTimeOffset TokenExpiresAtUtc);

public sealed record GitHubTrustedRemoteReadPlan(
    Guid SourceSnapshotId, string Organization, string Repository,
    string SourceEvidenceSha256, IReadOnlyList<GitMirrorRef> SourceRefs,
    string BaselineSha256, string CandidateSetSha256,
    string RefSetSha256, string PlanEvidenceSha256)
{
    // F-060 is materialized from F-038's persisted workpack bundle;
    // this ID is distinct from the F-029 source snapshot ID.
    public Guid SourceMirrorId { get; init; }
    public GitSourceBundleView? SourceBundle { get; init; }
}

public interface IGitHubTrustedRemoteReadPlanReader
{
    Task<GitHubTrustedRemoteReadPlan> ReadAsync(
        GitHubTrustedRemoteReadCommand command, CancellationToken ct);
}

public sealed class GitHubTrustedRemoteReadException(string code)
    : Exception("Trusted GitHub read-only orchestration refused")
{
    public string Code { get; } = code;
}

public sealed class GitHubTrustedRemoteReadCoordinator(
    IGitHubTrustedRemoteReadPlanReader plans,
    IGitHubRefObservationReader providerRefs,
    IGitHubReadOnlyObjectMirrorRunner mirror,
    IGitSourceSnapshotMirrorMaterializer materializer,
    IGitHubRemoteObjectReconciliationJournal journal,
    GitHubReadOnlyObjectMirrorOptions options,
    GitHubTrustedRemoteReadOptions orchestrationOptions,
    TimeProvider clock)
{
    public async Task<GitHubRemoteObjectReconciliationStatus> ExecuteAsync(
        GitHubTrustedRemoteReadCommand command,
        string ephemeralInstallationToken, CancellationToken ct)
    {
        if (!orchestrationOptions.Enabled)
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadDisabled");
        if (command is null || command.GrantId == Guid.Empty ||
            command.ReadbackId == Guid.Empty || command.HazardId == Guid.Empty ||
            command.RepositoryId <= 0 || command.ObserverId == Guid.Empty ||
            command.OriginalBaseline is null ||
            command.CreateCandidates is null ||
            string.IsNullOrWhiteSpace(ephemeralInstallationToken) ||
            ephemeralInstallationToken.Length > 1024 ||
            ephemeralInstallationToken.Any(char.IsControl))
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadInputInvalid");

        // The source plan and F-047/050/052/054/055 lineage are read
        // from PostgreSQL BEFORE contacting a provider or fetching Git.
        var before = await plans.ReadAsync(command, ct);
        ValidatePlan(before, command);
        var now = clock.GetUtcNow();
        if (command.TokenExpiresAtUtc <= now.AddMinutes(2) ||
            command.TokenExpiresAtUtc > now.AddMinutes(65))
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadTokenStale");

        // F-060 imports/rehashes the original F-038 source bundle
        // locally BEFORE any GitHub I/O. Missing or divergent physical
        // source mirrors cannot be treated as staged Git objects.
        var physicalBefore = await materializer.MaterializeAsync(before, ct);
        ValidatePhysical(physicalBefore, before);

        var observed = await providerRefs.ReadAsync(
            before.Organization, before.Repository,
            ephemeralInstallationToken, ct);
        GitHubRemotePostSendInspection classification;
        try
        {
            var baseline = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                command.OriginalBaseline.Select(x =>
                    new GitMirrorRef(x.Name, x.ObjectId)).ToArray());
            classification = GitHubRemotePostSendIntegrity.Inspect(
                baseline, command.CreateCandidates, observed);
        }
        catch (GitHubRemoteTestGrantException)
        {
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadRefsInvalid");
        }
        if (classification.State != "ExactRemoteRefsObserved" ||
            classification.RefSetSha256 != before.RefSetSha256)
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadRefsDrift");

        var request = new GitHubReadOnlyObjectMirrorRequest(
            before.SourceMirrorId, command.RepositoryId,
            before.Organization, before.Repository,
            before.SourceRefs, observed, command.TokenExpiresAtUtc);
        var receipt = await mirror.VerifyAsync(
            request, ephemeralInstallationToken, ct);
        // A slow proof must NOT silently convert expired evidence into an
        // accepted journal receipt. Plans are re-fetched to detect changes
        // to the source snapshot, source ref evidence and F-055 marker.
        var after = await plans.ReadAsync(command, ct);
        ValidatePlan(after, command);
        if (after.PlanEvidenceSha256 != before.PlanEvidenceSha256 ||
            after.SourceSnapshotId != before.SourceSnapshotId ||
            after.SourceMirrorId != before.SourceMirrorId ||
            after.SourceEvidenceSha256 != before.SourceEvidenceSha256 ||
            !after.SourceRefs.SequenceEqual(before.SourceRefs) ||
            after.RefSetSha256 != before.RefSetSha256)
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadPlanChanged");
        var physicalAfter = await materializer.MaterializeAsync(after, ct);
        ValidatePhysical(physicalAfter, after);
        if (physicalAfter.EvidenceSha256 != physicalBefore.EvidenceSha256)
            throw new GitHubTrustedRemoteReadException(
                "TrustedRemoteReadPhysicalSourceChanged");

        if (receipt.VerifiedAtUtc > clock.GetUtcNow().AddSeconds(5))
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadClockInvalid");

        // Only the output of the actual injected F-057 runner, never a
        // caller-provided receipt, reaches F-058. F-058 still independently
        // rechecks its PostgreSQL proof lineage under the global repo lock.
        return await journal.RecordAsync(new(
            command.GrantId, command.ReadbackId, command.HazardId,
            command.RepositoryId, command.ObserverId,
            command.OriginalBaseline, command.CreateCandidates,
            request, receipt), ct);
    }

    private static void ValidatePhysical(
        GitSourceSnapshotMirrorProof proof, GitHubTrustedRemoteReadPlan plan)
    {
        if (proof is null || proof.SourceSnapshotId != plan.SourceSnapshotId ||
            proof.PhysicalMirrorId != plan.SourceMirrorId ||
            proof.WorkpackId != plan.SourceBundle?.WorkpackId ||
            proof.BundleSha256 != plan.SourceBundle.BundleSha256 ||
            proof.SourceEvidenceSha256 != plan.SourceEvidenceSha256 ||
            proof.SourceRefSetSha256 !=
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(plan.SourceRefs) ||
            proof.RemotePushInvoked || proof.RemoteWritesAuthorized ||
            proof.MayAutomaticallyRetry || proof.MigrationComplete)
            throw new GitHubTrustedRemoteReadException(
                "TrustedRemoteReadPhysicalSourceMismatch");
    }

    private void ValidatePlan(GitHubTrustedRemoteReadPlan plan,
        GitHubTrustedRemoteReadCommand command)
    {
        if (plan is null || plan.SourceSnapshotId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.BaselineSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.CandidateSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.RefSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.PlanEvidenceSha256) ||
            plan.SourceRefs is null ||
            !options.Enabled || options.AllowedRepositoryId != command.RepositoryId ||
            !string.Equals(options.AllowedRepository,
                plan.Organization + "/" + plan.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadPlanInvalid");

        try
        {
            GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw new GitHubTrustedRemoteReadException(
                "TrustedRemoteReadPlanInvalid");
        }
        try
        {
            var source = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                plan.SourceRefs);
            var baseline = GitHubRefObservationIntegrity.Seal(
                plan.Organization, plan.Repository, clock.GetUtcNow(),
                command.OriginalBaseline);
            var candidates = GitHubRemoteTipIntegrity.CanonicalCandidates(
                command.CreateCandidates);
            if (!source.SequenceEqual(plan.SourceRefs) ||
                !baseline.Refs.SequenceEqual(command.OriginalBaseline) ||
                GitHubRemoteRefFenceIntegrity.Fingerprint(
                    command.OriginalBaseline) != plan.BaselineSha256 ||
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", candidates) !=
                    plan.CandidateSetSha256 ||
                plan.PlanEvidenceSha256 != Fingerprint(plan))
                throw new GitHubTrustedRemoteReadException(
                    "TrustedRemoteReadPlanInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadPlanInvalid");
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadPlanInvalid");
        }
        catch (GitHubRemoteTipException)
        {
            throw new GitHubTrustedRemoteReadException("TrustedRemoteReadPlanInvalid");
        }
    }

    public static string Fingerprint(GitHubTrustedRemoteReadPlan plan)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] {
                "f059-source-snapshot-plan-v1",
                plan.SourceSnapshotId.ToString("D"),
                plan.Organization.ToLowerInvariant(),
                plan.Repository.ToLowerInvariant(),
                plan.SourceEvidenceSha256,
                plan.SourceMirrorId.ToString("D"),
                plan.SourceBundle?.WorkpackId.ToString("D"),
                plan.SourceBundle?.SourceRepositoryId,
                plan.SourceBundle?.ReconciliationSha256,
                plan.SourceBundle?.BundleSha256,
                plan.SourceBundle?.BundleBytes,
                plan.SourceBundle?.RefCount,
                GitImportRehearsalIntegrity.SealRefs(
                    "f059-source-refs-v1", plan.SourceRefs),
                plan.BaselineSha256, plan.CandidateSetSha256,
                plan.RefSetSha256
            })));
        return Convert.ToHexStringLower(hash);
    }
}
