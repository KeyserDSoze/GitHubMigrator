namespace GitHubMigrator.Business;

// F-083 is an internal, explicitly enabled sandbox-only experiment.
// The caller supplies ONLY immutable confirmation/intent/plan IDs and an
// independent operator. No public HTTP route or background worker exists.
public sealed record GitHubSandboxOneShotOptions(
    bool Enabled, long AllowedRepositoryId,
    string AllowedOrganization, string AllowedRepository);

public sealed class GitHubSandboxOneShotException(string code)
    : Exception("GitHub sandbox one-shot operation refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubSandboxOneShotOutcome(
    Guid BarrierId, string Status, bool TransportMayHaveRun,
    bool RequiresIndependentReconciliation, bool CanRetry,
    bool MigrationComplete);

// Used only in the F-083 process call. Never persist a token, ref name,
// target URL, Git arguments or secret in an audit receipt.
public sealed record GitHubSandboxPushInput(
    GitHubCreateOnlyCandidatePacket Source,
    GitSourceBundleView Bundle,
    GitHubRemoteSandboxPreSendBarrierReceipt Barrier,
    string BaselineSha256, GitHubRefObservation BaselineRefs);

public interface IGitHubSandboxOneShotGitWriter
{
    Task PushOnceAsync(GitHubSandboxPushInput input,
        string oneRepositoryInstallationToken, CancellationToken ct);
}

public interface IGitHubSandboxVerifiedTokenScope
{
    Task<GitHubSandboxOneShotOutcome> WithVerifiedTokenAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, long expectedRepositoryId,
        Func<string, CancellationToken, Task<GitHubSandboxOneShotOutcome>> action,
        CancellationToken ct);
}

public interface IGitHubSandboxPreSendAuthority
{
    Task<string> GetBaselineAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, long repositoryId, CancellationToken ct);

    Task<GitHubRemoteSandboxPreSendBarrierReceipt> MarkOnceAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid operatorId, CancellationToken ct);
}

public static class GitHubSandboxPushIntegrity
{
    // Check inert, trusted F-044/F-082 lineage and exact test-repository
    // scope again inside the transport adapter, before ANY Git process.
    public static void Verify(GitHubSandboxPushInput request,
        GitHubSandboxOneShotOptions options)
    {
        var source = request?.Source;
        var p = source?.Packet;
        var b = request?.Bundle;
        var h = request?.Barrier;
        var baseline = request?.BaselineRefs;
        if (source is null || p is null || b is null || h is null ||
            baseline is null || baseline.Refs is null ||
            !options.Enabled || options.AllowedRepositoryId <= 0 ||
            !GitHubRefObservationIntegrity.ValidOwner(options.AllowedOrganization) ||
            !GitHubRefObservationIntegrity.ValidRepository(options.AllowedRepository) ||
            p.RepositoryId != options.AllowedRepositoryId ||
            h.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(p.Organization, options.AllowedOrganization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(p.Repository, options.AllowedRepository,
                StringComparison.OrdinalIgnoreCase) ||
            p.State != GitHubCreateOnlyPacketIntegrity.State ||
            p.ExecutionAuthorized || p.ReadyForExecution ||
            p.WriteCapabilityVerified || p.RemoteWritesPerformed ||
            p.EvidenceSha256 != h.PacketSha256 ||
            p.CandidateSetSha256 != h.CandidateSetSha256 ||
            p.WorkpackId != b.WorkpackId || p.PlanId != b.PlanId ||
            p.BundleSha256 != b.BundleSha256 ||
            p.ReconciliationSha256 != b.ReconciliationSha256 ||
            !b.IsCurrent || b.ReadyForExecution ||
            !GitSourceBundleStorageCheck.ValidSha(request.BaselineSha256) ||
            !string.Equals(baseline.Organization, p.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Repository, p.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            baseline.ObservedAtUtc < h.RecordedAtUtc.AddSeconds(-30) ||
            baseline.ObservedAtUtc > h.RecordedAtUtc.AddSeconds(5) ||
            source.Candidates is null ||
            source.Candidates.Count != p.CreateCandidates ||
            source.Candidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            source.Candidates.Any(x =>
                !x.Name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                !x.Name.StartsWith("refs/tags/", StringComparison.Ordinal)) ||
            !source.Candidates.SequenceEqual(
                GitMirrorRefReconciler.CanonicalizeObservedRefs(source.Candidates)) ||
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", source.Candidates) !=
                p.CandidateSetSha256)
            throw new GitHubSandboxOneShotException("SandboxPushEvidenceInvalid");
        try
        {
            GitHubRemoteSandboxPreSendBarrierIntegrity.Verify(h);
            var sealedBaseline = GitHubRefObservationIntegrity.Seal(
                baseline.Organization, baseline.Repository,
                baseline.ObservedAtUtc, baseline.Refs);
            if (sealedBaseline.EvidenceSha256 != baseline.EvidenceSha256 ||
                !sealedBaseline.Refs.SequenceEqual(baseline.Refs) ||
                GitHubRemoteRefFenceIntegrity.Fingerprint(baseline.Refs) !=
                    request.BaselineSha256 ||
                baseline.Refs.Any(x => source.Candidates.Any(c =>
                    c.Name == x.Name)))
                throw new GitHubSandboxOneShotException(
                    "SandboxPushEvidenceInvalid");
        }
        catch (Exception ex) when (
            ex is GitHubRemoteSandboxPreSendBarrierException or
                GitHubRefObservationException or
                GitHubRemoteRefFenceException or
                GitHubSandboxOneShotException)
        { throw new GitHubSandboxOneShotException("SandboxPushEvidenceInvalid"); }
    }
}

// Only AFTER an authenticated, one-repository GitHub App token exists,
// this service performs two fresh GitHub GET ref snapshots with a policy
// check in between. F-082 is committed AFTER those reads and an F-044
// source/disk recheck and BEFORE any Git subprocess can run.
public sealed class GitHubSandboxOneShotSender(
    GitHubSandboxOneShotOptions options,
    GitHubAppWriteCredentialOptions credentials,
    IConnectionSecretStore secrets,
    IGitHubCreateOnlyPacketEvidenceReader packets,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IGitHubSandboxVerifiedTokenScope tokens,
    IGitHubRefObservationReader refs,
    IGitHubCandidatePolicyReader policies,
    IGitHubSandboxPreSendAuthority authority,
    IGitHubRemoteTypedRecoveryBaselineJournal typedBaselines,
    IGitHubSandboxOneShotGitWriter writer,
    IGitHubRemoteTipObjectReader readback)
{
    public async Task<GitHubSandboxOneShotOutcome> SendOnceAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid independentOperator,
        CancellationToken ct)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            !credentials.Enabled || credentials.AppId <= 0 ||
            credentials.InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(credentials.PrivateKeySecretReference))
            throw new GitHubSandboxOneShotException("SandboxSenderDisabled");
        if (confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty || independentOperator == Guid.Empty)
            throw new GitHubSandboxOneShotException("SandboxSenderInputInvalid");

        var source = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        if (source.Packet.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(source.Packet.Organization, options.AllowedOrganization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Packet.Repository, options.AllowedRepository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubSandboxOneShotException("SandboxSenderWrongTarget");
        var bundle = await bundles.GetAsync(preparationId, planId,
            source.Packet.WorkpackId, ct) ??
            throw new GitHubSandboxOneShotException("SandboxSenderBundleMissing");
        var verified = await disk.VerifyAsync(bundle, ct);
        if (!verified.IsCurrent ||
            verified.BundleSha256 != source.Packet.BundleSha256 ||
            verified.BundleBytes != bundle.BundleBytes)
            throw new GitHubSandboxOneShotException("SandboxSenderBundleChanged");

        // Read the signing key using the server-only configured secret
        // reference, never from HTTP input or caller-supplied command.
        var key = await secrets.ReadAsync(
            credentials.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 32768)
            throw new GitHubSandboxOneShotException("SandboxSenderSigningKeyMissing");

        return await tokens.WithVerifiedTokenAsync(
            credentials.AppId, credentials.InstallationId, key,
            options.AllowedOrganization, options.AllowedRepository,
            options.AllowedRepositoryId,
            async (token, tokenCt) =>
            {
                var baseline = await authority.GetBaselineAsync(
                    confirmationId, intentId, preparationId,
                    planId, scopeId, options.AllowedRepositoryId, tokenCt);
                var before = await refs.ReadAsync(options.AllowedOrganization,
                    options.AllowedRepository, token, tokenCt);
                VerifyRemoteBaseline(before, baseline, source);
                var observed = await policies.ReadAsync(
                    options.AllowedOrganization, options.AllowedRepository,
                    options.AllowedRepositoryId, token, source.Candidates, tokenCt);
                var policy = GitHubCandidatePolicyIntegrity.Seal(source, observed);
                if (policy.Status != "AdvisoryNoRulesReported" ||
                    policy.NoActiveRulesReported != source.Candidates.Count ||
                    policy.RulesRequireReview != 0 || policy.PolicyUnverified != 0)
                    throw new GitHubSandboxOneShotException("SandboxSenderPolicyUnverified");
                var second = await refs.ReadAsync(options.AllowedOrganization,
                    options.AllowedRepository, token, tokenCt);
                VerifyRemoteBaseline(second, baseline, source);
                if (!before.Refs.SequenceEqual(second.Refs))
                    throw new GitHubSandboxOneShotException("SandboxSenderRemoteDrift");

                // Recreate server-side F-044 and rehash bundle after GETs.
                // A changed source or bundle never consumes F-082.
                var final = await packets.PreviewWithCandidatesAsync(
                    preparationId, planId, scopeId, tokenCt);
                var physical = await disk.VerifyAsync(bundle, tokenCt);
                if (final.Packet.EvidenceSha256 != source.Packet.EvidenceSha256 ||
                    final.Packet.CandidateSetSha256 != source.Packet.CandidateSetSha256 ||
                    !final.Candidates.SequenceEqual(source.Candidates) ||
                    !physical.IsCurrent ||
                    physical.BundleSha256 != bundle.BundleSha256 ||
                    physical.BundleBytes != bundle.BundleBytes)
                    throw new GitHubSandboxOneShotException("SandboxSenderSourceDrift");

                // F-086: persist a typed, immutable recovery snapshot
                // BEFORE the irreversible F-082 commit. If this fails,
                // the sender must refuse to send anything. A future
                // independent reader can reconstitute the exact original
                // branch/tag types and candidate OIDs after a crash,
                // without accepting caller-supplied refs or any token.
                await typedBaselines.CaptureAsync(
                    confirmationId, intentId, preparationId, planId,
                    scopeId, independentOperator, final, second, tokenCt);

                // This commit is the irreversible point. Cancellation,
                // process crash and lost acknowledgment after this point
                // ALWAYS require reconciliation; retry is forbidden.
                var marker = await authority.MarkOnceAsync(
                    confirmationId, intentId, preparationId, planId, scopeId,
                    independentOperator, tokenCt);
                var request = new GitHubSandboxPushInput(
                    source, bundle, marker, baseline, second);
                var attempted = false;
                try
                {
                    GitHubSandboxPushIntegrity.Verify(request, options);
                    attempted = true;
                    await writer.PushOnceAsync(request, token, tokenCt);
                    // Git process zero exit is NEVER enough to prove object
                    // receipt. GET the typed remote refs and commit/tag tips;
                    // a separate object-closure audit is still mandatory.
                    var tips = await readback.ReadAsync(
                        options.AllowedOrganization, options.AllowedRepository,
                        token, source.Candidates, tokenCt);
                    if (tips is null || tips.TipReadback is null)
                        return new(marker.Id,
                            "SandboxRemoteOutcomeRequiresReconciliation",
                            attempted, true, false, false);
                    var prior = second.Refs.Select(x =>
                        new GitMirrorRef(x.Name, x.ObjectId)).ToArray();
                    var proof = GitHubRemoteTipIntegrity.Inspect(
                        prior, source.Candidates,
                        tips.RemoteRefs, tips.TipReadback);
                    if (proof.State != "ExactRemoteRefsAndTipObjectsObserved")
                        return new(marker.Id,
                            "SandboxRemoteOutcomeRequiresReconciliation",
                            attempted, true, false, false);
                    return new(marker.Id,
                        "SandboxRemoteTipsObservedNeedsClosureAudit",
                        true, true, false, false);
                }
                catch (Exception ex) when (
                    ex is not OutOfMemoryException &&
                    ex is not StackOverflowException)
                {
                    // Never echo Git stderr, token, HTTP response or URL.
                    return new(marker.Id,
                        "SandboxRemoteOutcomeRequiresReconciliation",
                        attempted, true, false, false);
                }
            }, ct);
    }

    private static void VerifyRemoteBaseline(
        GitHubRefObservation observation, string expected,
        GitHubCreateOnlyCandidatePacket packet)
    {
        if (observation is null ||
            !string.Equals(observation.Organization,
                packet.Packet.Organization, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.Repository,
                packet.Packet.Repository, StringComparison.OrdinalIgnoreCase) ||
            GitHubRemoteRefFenceIntegrity.Fingerprint(observation.Refs) != expected ||
            observation.Refs.Any(x => packet.Candidates.Any(c => x.Name == c.Name)))
            throw new GitHubSandboxOneShotException("SandboxSenderRemoteDrift");
    }
}
