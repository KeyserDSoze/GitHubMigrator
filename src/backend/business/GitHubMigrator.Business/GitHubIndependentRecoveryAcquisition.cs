namespace GitHubMigrator.Business;

// F-087: independent recovery is a MANUAL, read-only evidence operation
// scoped to the immutable F-086 historical baseline AFTER the F-082 marker.
// No F-083 sender, writer, queue, upload or retry is reachable here.
public sealed record GitHubIndependentRecoveryOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed class GitHubIndependentRecoveryException(string code)
    : Exception("Independent GitHub post-send evidence acquisition refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubIndependentRecoveryResult(
    Guid HazardId, Guid GrantId, Guid? ReadbackId, Guid? ReconciliationId,
    string State, string? EvidenceSha256, bool ReadOnlyAcquisition,
    bool CanPush, bool CanRetry, bool MigrationComplete);

public static class GitHubIndependentRecoveryStates
{
    public const string ReadbackCaptured = "IndependentRemoteTipsCaptured";
    public const string OperatorReview = "RemoteTipMismatchNeedsReview";
    public const string ClosureCaptured = "IndependentFullObjectClosureCaptured";
    public const string NoNewEvidence = "ExistingIndependentEvidence";
}

// Restricted installation token stays IN MEMORY inside one callback;
// returned DTOs never contain it. Must mint exactly Contents:read,
// Metadata:read for the one verified GitHub numeric repository.
public interface IGitHubRecoveryReadOnlyTokenScope
{
    Task<GitHubIndependentRecoveryResult> WithReadOnlyTokenAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, long repositoryId,
        Func<string, DateTimeOffset, CancellationToken,
            Task<GitHubIndependentRecoveryResult>> action,
        CancellationToken ct);
}

// Independent F-054 insert is distinct from legacy pre-send F-054: a
// committed F-082 marker deliberately invalidates old grant deadlines.
public interface IGitHubRemoteRecoveredReadbackJournal
{
    Task<GitHubRemoteReadbackStatus> RecordRecoveredAsync(
        GitHubRemoteTypedRecoveryBaselineReceipt typed,
        GitHubRemoteReadbackEvidence evidence, CancellationToken ct);
}

public interface IGitHubTrustedRemoteReadExecutor
{
    Task<GitHubRemoteObjectReconciliationStatus> ExecuteAsync(
        GitHubTrustedRemoteReadCommand command,
        string ephemeralInstallationToken, CancellationToken ct);
}

public sealed class GitHubIndependentRecoveryAcquisition(
    GitHubIndependentRecoveryOptions options,
    GitHubAppWriteCredentialOptions credentialOptions,
    IConnectionSecretStore secrets,
    IGitHubRemoteTypedRecoveryBaselineJournal typed,
    IGitHubRecoveryReadOnlyTokenScope tokens,
    IGitHubRemoteTipObjectReader tips,
    IGitHubRemoteReadbackJournal readback,
    IGitHubRemoteRecoveredReadbackJournal recoveredReadback,
    IGitHubRemoteObjectReconciliationJournal reconciliations,
    IGitHubTrustedRemoteReadExecutor fullClosure)
{
    public async Task<GitHubIndependentRecoveryResult> AcquireTipsAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid observerId, CancellationToken ct)
    {
        var baseline = await RequireHistoricalAsync(confirmationId,
            intentId, preparationId, planId, scopeId, observerId, ct);
        var previous = await readback.GetAsync(
            baseline.GrantId, baseline.RepositoryId, ct);
        if (previous is not null)
        {
            if (previous.ObservedAtUtc < baseline.PossibleSendRecordedAtUtc)
                throw Refused("IndependentRecoveryOldReadback");
            return TipStatus(baseline, previous);
        }
        return await WithTokenAsync(baseline, async (token, expiry, tokenCt) =>
        {
            // Only the reader's GitHub REST GET operations can run. Both
            // the original typed refs and create candidate OIDs come from
            // the immutable F-086 journal, never the caller/request.
            var remote = await tips.ReadAsync(
                baseline.Organization, baseline.Repository,
                token, baseline.CreateCandidates, tokenCt);
            var evidence = new GitHubRemoteReadbackEvidence(
                baseline.GrantId, baseline.RepositoryId, observerId,
                baseline.Baseline.Refs, baseline.CreateCandidates, remote);
            var stored = await recoveredReadback.RecordRecoveredAsync(
                baseline, evidence, tokenCt);
            return TipStatus(baseline, stored);
        }, ct);
    }

    public async Task<GitHubIndependentRecoveryResult> AcquireClosureAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid observerId, CancellationToken ct)
    {
        var baseline = await RequireHistoricalAsync(confirmationId,
            intentId, preparationId, planId, scopeId, observerId, ct);
        var observed = await readback.GetAsync(
            baseline.GrantId, baseline.RepositoryId, ct);
        if (observed is null ||
            observed.State != GitHubRemoteReadbackStates.ExactTipEvidenceOnly ||
            observed.BaselineSha256 != baseline.BaselineSha256 ||
            observed.CandidateSetSha256 != baseline.CandidateSetSha256 ||
            observed.ObservedBy == observerId ||
            !GitSourceBundleStorageCheck.ValidSha(observed.RefSetSha256))
            throw Refused("IndependentRecoveryExactTipsRequired");
        var prior = await reconciliations.GetAsync(
            baseline.HazardId, baseline.RepositoryId, ct);
        if (prior is not null)
        {
            GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(prior);
            return new(baseline.HazardId, baseline.GrantId,
                observed.Id, prior.Id,
                GitHubIndependentRecoveryStates.NoNewEvidence,
                prior.EvidenceSha256, true, false, false, false);
        }
        return await WithTokenAsync(baseline, async (token, expiry, tokenCt) =>
        {
            var closure = await fullClosure.ExecuteAsync(
                new(baseline.GrantId, observed.Id, baseline.HazardId,
                    baseline.RepositoryId, observerId,
                    baseline.Baseline.Refs, baseline.CreateCandidates, expiry),
                token, tokenCt);
            if (closure.State !=
                    GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly ||
                closure.RemotePushInvoked || closure.RemoteWritesAuthorized ||
                closure.MayAutomaticallyRetry || closure.MigrationComplete)
                throw Refused("IndependentRecoveryClosureInvalid");
            return new(baseline.HazardId, baseline.GrantId,
                observed.Id, closure.Id,
                GitHubIndependentRecoveryStates.ClosureCaptured,
                closure.EvidenceSha256, true, false, false, false);
        }, ct);
    }

    private async Task<GitHubRemoteTypedRecoveryBaselineReceipt>
        RequireHistoricalAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid observerId, CancellationToken ct)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository) ||
            !credentialOptions.Enabled ||
            credentialOptions.AppId <= 0 ||
            credentialOptions.InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(
                credentialOptions.PrivateKeySecretReference))
            throw Refused("IndependentRecoveryDisabled");
        if (new[] { confirmationId, intentId, preparationId, planId,
                scopeId, observerId }.Any(x => x == Guid.Empty))
            throw Refused("IndependentRecoveryInputInvalid");
        var snapshot = await typed.ReadAfterBarrierAsync(
            confirmationId, intentId, preparationId,
            planId, scopeId, ct) ??
            throw Refused("IndependentRecoveryNoCommittedBarrier");
        try { GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(snapshot); }
        catch (GitHubRemoteTypedRecoveryBaselineException)
        { throw Refused("IndependentRecoveryHistoryCorrupt"); }
        if (snapshot.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(options.AllowedRepository,
                snapshot.Organization + "/" + snapshot.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(credentialOptions.AllowedRepository,
                options.AllowedRepository, StringComparison.OrdinalIgnoreCase) ||
            snapshot.CapturedBy == observerId || snapshot.CanSend ||
            snapshot.MayAutomaticallyRetry || snapshot.MigrationComplete)
            throw Refused("IndependentRecoveryWrongTargetOrActor");
        return snapshot;
    }

    private async Task<GitHubIndependentRecoveryResult> WithTokenAsync(
        GitHubRemoteTypedRecoveryBaselineReceipt baseline,
        Func<string, DateTimeOffset, CancellationToken,
            Task<GitHubIndependentRecoveryResult>> action, CancellationToken ct)
    {
        var key = await secrets.ReadAsync(
            credentialOptions.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(key))
            throw Refused("IndependentRecoveryPrivateKeyMissing");
        return await tokens.WithReadOnlyTokenAsync(
            credentialOptions.AppId, credentialOptions.InstallationId, key,
            baseline.Organization, baseline.Repository, baseline.RepositoryId,
            action, ct);
    }

    private static GitHubIndependentRecoveryResult TipStatus(
        GitHubRemoteTypedRecoveryBaselineReceipt baseline,
        GitHubRemoteReadbackStatus status) =>
        new(baseline.HazardId, baseline.GrantId, status.Id, null,
            status.State == GitHubRemoteReadbackStates.ExactTipEvidenceOnly
                ? GitHubIndependentRecoveryStates.ReadbackCaptured
                : GitHubIndependentRecoveryStates.OperatorReview,
            status.JournalEvidenceSha256, true, false, false, false);

    private static GitHubIndependentRecoveryException Refused(string code) =>
        new(code);
}
