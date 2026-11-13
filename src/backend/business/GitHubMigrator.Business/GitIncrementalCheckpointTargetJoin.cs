using GitHubMigrator.Business;

namespace GitHubMigrator.Business;

// #104 after #109: a VERSION 2, read-only join of independently verified
// latest F-091/F-097b and checkpoint-scoped F-031. This domain transform
// does NOT fetch provenance from PostgreSQL and is NEVER admission to
// F-032/F-038/F-035. A future source-locked journal must independently
// verify the current epoch, immutable target audit and mapping before use.
public static class GitIncrementalCheckpointTargetJoin
{
    private static GitIncrementalCheckpointPreflightException Refused() =>
        new("GitCheckpointTargetJoinInvalid");

    private static bool Sha(string? digest) =>
        digest is { Length: 64 } &&
        digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitIncrementalCheckpointAuditInputs ForReadOnlyComparison(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        GitHubSourceCheckpointGitRefProof source,
        GitCheckpointTargetObservation target)
    {
        if (plan is null || checkpoint is null || source is null ||
            target is null)
            throw Refused();

        try
        {
            GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(checkpoint);
            GitCheckpointTargetObservationIntegrity.VerifyStored(target);
        }
        catch (Exception ex) when (ex is GitHubSourceSyncCheckpointException
            or GitCheckpointTargetObservationException)
        { throw Refused(); }

        var epoch = checkpoint.Proof;
        if (plan.Id == Guid.Empty || plan.PreparationId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty ||
            plan.DestinationConnectionId == Guid.Empty ||
            plan.IsCurrent || plan.ReadyForExecution ||
            checkpoint.EvidenceVersion != 2 ||
            checkpoint.RefChangeManifest is null ||
            !Sha(checkpoint.RefChangeAttestationSha256) ||
            epoch.IsBaselineSnapshot || epoch.PreviousCheckpointId is null ||
            epoch.SnapshotId == plan.SnapshotId ||
            epoch.PreparationId != plan.PreparationId ||
            epoch.PlanId != plan.Id ||
            epoch.SourceConnectionId != plan.SourceConnectionId ||
            epoch.BaselineSnapshotId != plan.SnapshotId ||
            epoch.ManifestSha256 != plan.ManifestSha256 ||
            source.EvidenceVersion != 1 ||
            source.CheckpointId != checkpoint.Id ||
            source.PreparationId != plan.PreparationId ||
            source.PlanId != plan.Id ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.SnapshotId != epoch.SnapshotId ||
            source.BaselineSnapshotId != plan.SnapshotId ||
            source.PreviousCheckpointId != epoch.PreviousCheckpointId ||
            !Sha(source.PreviousFullRefProofSha256) ||
            source.CheckpointEvidenceSha256 != checkpoint.EvidenceSha256 ||
            source.CheckpointRefAttestationSha256 != checkpoint.RefChangeAttestationSha256 ||
            !Sha(source.EvidenceSha256) ||
            !Sha(source.SourceEvidenceSha256) ||
            !Sha(source.SourceManifestRootSha256) ||
            source.CanPush || source.CanRetry || source.CanDispatch ||
            source.MigrationComplete ||
            target.PreparationId != plan.PreparationId ||
            target.PlanId != plan.Id ||
            target.CheckpointId != checkpoint.Id ||
            target.SourceSnapshotId != epoch.SnapshotId ||
            target.DestinationConnectionId != plan.DestinationConnectionId ||
            target.SourceRepositoryId != source.SourceRepositoryId ||
            !string.Equals(target.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            target.ObservedAtUtc < epoch.StagingAppliedAtUtc ||
            plan.Steps.Count(s => s.Kind == "ReviewTargetName" &&
                s.SourceRepositoryId == source.SourceRepositoryId &&
                s.TargetRepositoryName == target.TargetRepositoryName) != 1 ||
            source.Refs is not { Count: >= 1 } ||
            source.Refs.Count > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            target.Refs is not { Count: >= 1 } ||
            target.Refs.Count > GitIncrementalAncestryIntegrity.MaxRefsPerSide)
            throw Refused();

        IReadOnlyList<GitMirrorRef> sourceRefs;
        IReadOnlyList<GitMirrorRef> targetRefs;
        try
        {
            sourceRefs = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                source.Refs);
            targetRefs = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                target.Refs.Select(r => new GitMirrorRef(r.Name, r.ObjectId))
                    .ToArray());
            if (!sourceRefs.SequenceEqual(source.Refs) ||
                sourceRefs.Any(r => r.ObjectId.Length !=
                    (target.ObjectFormat == "sha1" ? 40 : 64)) ||
                targetRefs.Any(r => r.ObjectId.Length != sourceRefs[0].ObjectId.Length))
                throw Refused();

            // Ref-name case aliases across source and target can invalidate
            // even an otherwise well-formed source or destination set.
            _ = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256,
                source.SourceEvidenceSha256, target.EvidenceSha256,
                sourceRefs, targetRefs));
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused(); }

        // V2 deliberately cannot be passed to the current V1 workpack
        // writer: the #104 writer remains fail-closed until its own
        // source-locked PostgreSQL adapter is upgraded and tested.
        // Bind the FULL checkpoint F-031 receipt, not merely its ref hash,
        // so reviewer and candidate provenance cannot be swapped.
        return new(2, plan.PreparationId, plan.Id, plan.SourceConnectionId,
            source.SnapshotId, checkpoint.Id,
            checkpoint.RefChangeAttestationSha256!, source.EvidenceSha256,
            target.Id, target.EvidenceSha256, source.SourceRepositoryId,
            target.TargetRepositoryName, target.ObjectFormat,
            sourceRefs.ToArray(), targetRefs.ToArray(),
            false, false, false, false);
    }
}
