namespace GitHubMigrator.Business;

// F-097c.2 is an INTERNAL, read-only staging contract for a FUTURE
// F-095 v3 audit. It is not a GitHub remote read or execution permit.
public sealed record GitIncrementalCheckpointAuditInputs(
    Guid PreparationId, Guid PlanId, Guid SourceConnectionId,
    Guid SourceSnapshotId, Guid SourceCheckpointId,
    string SourceCheckpointAttestationSha256,
    string SourceFullRefProofSha256, Guid TargetObservationId,
    string TargetEvidenceSha256, string SourceRepositoryId,
    string TargetRepositoryName, string GitObjectFormat,
    IReadOnlyList<GitMirrorRef> ExpectedSourceRefs,
    IReadOnlyList<GitMirrorRef> ExpectedTargetRefs,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed class GitIncrementalCheckpointPreflightException(string code)
    : Exception("Incremental Git checkpoint ancestry preflight refused")
{
    public string Code { get; } = code;
}

public sealed record GitIncrementalCheckpointPreflightOptions(bool Enabled);

public static class GitIncrementalCheckpointAuditPreflightIntegrity
{
    private static GitIncrementalCheckpointPreflightException Refused() =>
        new("GitCheckpointAuditEvidenceInvalid");

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // The persistence adapter must have already independently verified
    // F-091/F-097b (including the complete F-014 source and prior chain)
    // and the sealed F-031 target observation. Never call with caller
    // supplied refs or an arbitrary self-sealed F-097 receipt.
    public static GitIncrementalCheckpointAuditInputs Seal(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        GitHubSourceCheckpointGitRefProof fullRefs,
        GitHubTargetRefEvidenceView target,
        GitHubRefObservation observation)
    {
        if (plan is null || checkpoint is null || fullRefs is null ||
            target is null || observation is null)
            throw Refused();
        try { GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(checkpoint); }
        catch (GitHubSourceSyncCheckpointException) { throw Refused(); }

        GitHubRefObservation actualTarget;
        IReadOnlyList<GitMirrorRef> sources;
        IReadOnlyList<GitMirrorRef> targets;
        try
        {
            actualTarget = GitHubRefObservationIntegrity.Seal(
                observation.Organization, observation.Repository,
                observation.ObservedAtUtc, observation.Refs);
            sources = GitMirrorRefReconciler.CanonicalizeObservedRefs(fullRefs.Refs);
            targets = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                actualTarget.AsMirrorRefs());
        }
        catch (Exception ex) when (ex is
            GitHubRefObservationException or
            GitMirrorRefReconciliationException or
            IncrementalGitRefReconciliationException)
        { throw Refused(); }

        if (sources.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            targets.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide)
            throw Refused();

        var oidLength = sources[0].ObjectId.Length;
        var all = sources.Concat(targets).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in all)
        {
            if (reference.ObjectId.Length != oidLength ||
                (names.TryGetValue(reference.Name, out var existing) &&
                 existing != reference.Name))
                throw Refused();
            names[reference.Name] = reference.Name;
        }

        var mapped = plan.Steps.Where(x => x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == fullRefs.SourceRepositoryId &&
            x.TargetRepositoryName == target.TargetRepositoryName).ToArray();
        if (!plan.IsCurrent || plan.ReadyForExecution ||
            plan.Id == Guid.Empty || plan.PreparationId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty ||
            checkpoint.EvidenceVersion != 2 ||
            checkpoint.RefChangeManifest is null ||
            !Sha(checkpoint.RefChangeAttestationSha256) ||
            checkpoint.Proof.PreparationId != plan.PreparationId ||
            checkpoint.Proof.PlanId != plan.Id ||
            checkpoint.Proof.SourceConnectionId != plan.SourceConnectionId ||
            checkpoint.Proof.BaselineSnapshotId != plan.SnapshotId ||
            checkpoint.Proof.ManifestSha256 != plan.ManifestSha256 ||
            fullRefs.EvidenceVersion != 1 ||
            !Sha(fullRefs.EvidenceSha256) ||
            fullRefs.CheckpointId != checkpoint.Id ||
            fullRefs.CheckpointEvidenceSha256 != checkpoint.EvidenceSha256 ||
            fullRefs.CheckpointRefAttestationSha256 !=
                checkpoint.RefChangeAttestationSha256 ||
            fullRefs.PreparationId != plan.PreparationId ||
            fullRefs.PlanId != plan.Id ||
            fullRefs.SourceConnectionId != plan.SourceConnectionId ||
            fullRefs.BaselineSnapshotId != plan.SnapshotId ||
            fullRefs.SnapshotId != checkpoint.Proof.SnapshotId ||
            fullRefs.PreviousCheckpointId != checkpoint.Proof.PreviousCheckpointId ||
            !Sha(fullRefs.SourceManifestRootSha256) ||
            !Sha(fullRefs.SourceEvidenceSha256) ||
            fullRefs.CanPush || fullRefs.CanRetry ||
            fullRefs.CanDispatch || fullRefs.MigrationComplete ||
            !sources.SequenceEqual(fullRefs.Refs) ||
            !targets.SequenceEqual(actualTarget.AsMirrorRefs()) ||
            mapped.Length != 1 ||
            target.Id == Guid.Empty || target.PlanId != plan.Id ||
            target.SourceRepositoryId != fullRefs.SourceRepositoryId ||
            target.ManifestSha256 != plan.ManifestSha256 ||
            target.RefCount != actualTarget.Refs.Count ||
            target.EvidenceSha256 != actualTarget.EvidenceSha256 ||
            target.ObservedAtUtc != actualTarget.ObservedAtUtc ||
            target.ObservedAtUtc < checkpoint.Proof.StagingAppliedAtUtc ||
            target.ReadyForExecution || !target.IsCurrent ||
            !string.Equals(target.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            observation.Repository != target.TargetRepositoryName)
            throw Refused();

        return new(
            plan.PreparationId, plan.Id, plan.SourceConnectionId,
            fullRefs.SnapshotId, checkpoint.Id,
            checkpoint.RefChangeAttestationSha256!,
            fullRefs.EvidenceSha256, target.Id, target.EvidenceSha256,
            fullRefs.SourceRepositoryId, target.TargetRepositoryName,
            oidLength == 40 ? "sha1" : "sha256",
            sources.ToArray(), targets.ToArray(),
            false, false, false, false);
    }
}
