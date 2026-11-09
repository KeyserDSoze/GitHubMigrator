namespace GitHubMigrator.Business;

// F-095: immutable, source-only registry of a physical F-094 audit, bound
// to a verified F-091 checkpoint and F-031 destination observation.
// Not a GitHub.com execution grant or live remote closure proof.
public sealed record GitIncrementalAncestryLedgerEntry(
    Guid Id, Guid PreparationId, Guid PlanId,
    string SourceRepositoryId, string TargetRepositoryName,
    string OperationManifestSha256, string SourceEvidenceSha256,
    GitIncrementalAncestryReceipt Receipt,
    Guid RecordedBy, DateTimeOffset RecordedAtUtc,
    bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitIncrementalAncestryLedgerException(string code)
    : Exception("Durable offline ancestry evidence refused")
{
    public string Code { get; } = code;
}

public sealed record GitIncrementalAncestryLedgerOptions(bool Enabled);

public interface IGitIncrementalAncestryLedger
{
    Task<GitIncrementalAncestryLedgerEntry> AuditAndRecordAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256,
        Guid targetSandboxId, Guid actorId, CancellationToken ct);

    Task<GitIncrementalAncestryLedgerEntry?> GetAsync(
        Guid preparationId, Guid planId, Guid ledgerId,
        CancellationToken ct);
}

public static class GitIncrementalAncestryLedgerIntegrity
{
    private static GitIncrementalAncestryLedgerException Refused() =>
        new("GitAncestryLedgerEvidenceInvalid");

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void VerifyBinding(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        VerifiedSourceGitRefs source,
        GitHubTargetRefEvidenceView target,
        GitHubRefObservation observation,
        GitIncrementalAncestryReceipt audit,
        bool requireCurrent)
    {
        if (plan is null || checkpoint is null || source is null ||
            target is null || observation is null || audit is null)
            throw Refused();
        try
        {
            GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(checkpoint);
            GitIncrementalAncestryIntegrity.VerifyReceipt(audit);
        }
        catch (Exception e) when (e is GitHubSourceSyncCheckpointException
            or GitIncrementalAncestryException)
        { throw Refused(); }

        var req = audit.Request;
        var match = plan.Steps.Where(x => x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == source.SourceRepositoryId &&
            x.TargetRepositoryName == target.TargetRepositoryName).ToArray();
        var sealedSource = VerifiedSourceGitRefIntegrity.Seal(
            source.SnapshotId, source.SourceConnectionId,
            source.SourceRepositoryId, source.RefManifestRootSha256,
            source.Refs);
        GitHubRefObservation sealedTarget;
        try { sealedTarget = GitHubRefObservationIntegrity.Seal(
            observation.Organization, observation.Repository,
            observation.ObservedAtUtc, observation.Refs); }
        catch (GitHubRefObservationException) { throw Refused(); }

        if ((requireCurrent && (!plan.IsCurrent || !target.IsCurrent)) ||
            plan.ReadyForExecution || target.ReadyForExecution ||
            checkpoint.EvidenceVersion != 2 ||
            checkpoint.RefChangeManifest is null ||
            !Sha(checkpoint.RefChangeAttestationSha256) ||
            checkpoint.Proof.PreparationId != plan.PreparationId ||
            checkpoint.Proof.PlanId != plan.Id ||
            checkpoint.Proof.ManifestSha256 != plan.ManifestSha256 ||
            // F-095 v1 supports only the sealed baseline F-029 snapshot.
            // Later F-090 epochs need full immutable F-014 ref manifests
            // for THAT epoch before their OIDs can be compared physically.
            checkpoint.Proof.SnapshotId != plan.SnapshotId ||
            !checkpoint.Proof.IsBaselineSnapshot ||
            checkpoint.Proof.SourceConnectionId != plan.SourceConnectionId ||
            source.SnapshotId != plan.SnapshotId ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            // A legacy inline or partial inventory is not an F-014
            // complete immutable ref manifest. Fail closed for v1.
            !Sha(source.RefManifestRootSha256) ||
            source.SourceEvidenceSha256 != sealedSource.SourceEvidenceSha256 ||
            match.Length != 1 ||
            target.PlanId != plan.Id ||
            target.ManifestSha256 != plan.ManifestSha256 ||
            target.SourceRepositoryId != source.SourceRepositoryId ||
            target.Organization != plan.Organization ||
            target.RefCount != observation.Refs.Count ||
            target.EvidenceSha256 != sealedTarget.EvidenceSha256 ||
            !string.Equals(observation.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            observation.Repository != target.TargetRepositoryName ||
            target.ObservedAtUtc != observation.ObservedAtUtc ||
            target.ObservedAtUtc < checkpoint.Proof.StagingAppliedAtUtc ||
            req.SourceSnapshotId != checkpoint.Proof.SnapshotId ||
            req.SourceCheckpointId != checkpoint.Id ||
            req.SourceCheckpointAttestationSha256 !=
                checkpoint.RefChangeAttestationSha256 ||
            req.TargetObservationId != target.Id ||
            req.TargetObservationSha256 != target.EvidenceSha256 ||
            !req.ExpectedSourceRefs.SequenceEqual(sealedSource.Refs) ||
            !req.ExpectedTargetRefs.SequenceEqual(
                sealedTarget.AsMirrorRefs()))
            throw Refused();
    }

    public static void VerifyEntry(
        GitIncrementalAncestryLedgerEntry entry,
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        VerifiedSourceGitRefs source,
        GitHubTargetRefEvidenceView target,
        GitHubRefObservation observation)
    {
        if (entry is null || entry.Id == Guid.Empty ||
            entry.RecordedBy == Guid.Empty ||
            entry.RecordedAtUtc == default ||
            entry.CanPush || entry.CanRetry ||
            entry.CanDispatch || entry.MigrationComplete ||
            entry.PreparationId != plan.PreparationId ||
            entry.PlanId != plan.Id ||
            entry.OperationManifestSha256 != plan.ManifestSha256 ||
            entry.SourceRepositoryId != source.SourceRepositoryId ||
            entry.TargetRepositoryName != target.TargetRepositoryName ||
            !Sha(entry.SourceEvidenceSha256) ||
            entry.SourceEvidenceSha256 != source.SourceEvidenceSha256)
            throw Refused();
        VerifyBinding(plan, checkpoint, source, target,
            observation, entry.Receipt, false);
    }
}
