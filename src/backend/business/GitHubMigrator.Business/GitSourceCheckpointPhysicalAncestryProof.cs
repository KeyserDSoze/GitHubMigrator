namespace GitHubMigrator.Business;

// F-097c.1: offline, read-only bridge of independently loaded F-091,
// F-097a/b and F-061 evidence into a real F-094 v3 physical audit.
// Does not acquire a mirror, certify a remote GitHub repository, persist
// F-095 or authorize sending any Git objects.
public static class GitSourceCheckpointPhysicalAncestryIntegrity
{
    private static GitIncrementalAncestryLedgerException Invalid() =>
        new("GitAncestryCheckpointMirrorEvidenceInvalid");

    public static void Verify(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        VerifiedSourceGitRefs source,
        GitHubSourceCheckpointGitRefProof fullRefProof,
        GitHubSourceCheckpointGitRefProof? predecessor,
        GitIncrementalSourceMirrorBinding mirror,
        GitIncrementalAncestryReceipt audit,
        DateTimeOffset now, bool requireFresh)
    {
        if (plan is null || checkpoint is null || source is null ||
            fullRefProof is null || mirror is null || audit is null)
            throw Invalid();

        try
        {
            // The caller must derive checkpoint/source/predecessor/mirror from
            // *independent* verified PostgreSQL rows. Self-sealed arbitrary
            // input is insufficient: F-097b.GetVerifiedAsync is required.
            GitHubSourceCheckpointGitRefProofIntegrity.Verify(
                fullRefProof, checkpoint, source, predecessor);
            if (plan.Id != fullRefProof.PlanId ||
                plan.PreparationId != fullRefProof.PreparationId ||
                plan.SourceConnectionId != fullRefProof.SourceConnectionId ||
                plan.SnapshotId != fullRefProof.BaselineSnapshotId ||
                plan.ReadyForExecution ||
                !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                    x.SourceRepositoryId == source.SourceRepositoryId) ||
                audit.Request.SourceCheckpointId != checkpoint.Id ||
                audit.Request.SourceCheckpointAttestationSha256 !=
                    checkpoint.RefChangeAttestationSha256 ||
                audit.Request.SourceCheckpointGitRefProofSha256 !=
                    fullRefProof.EvidenceSha256)
                throw Invalid();

            GitIncrementalSourceMirrorBindingIntegrity.VerifySnapshotContext(
                plan, source, mirror, fullRefProof.SnapshotId,
                now, requireFresh);
            GitIncrementalAncestryIntegrity.VerifyReceipt(audit);
            if (audit.Request.SourceSnapshotId != fullRefProof.SnapshotId ||
                audit.Request.SourcePhysicalMirrorId != mirror.MirrorId ||
                audit.Request.SourcePhysicalMirrorProofSha256 !=
                    mirror.MirrorProofSha256 ||
                !audit.Request.ExpectedSourceRefs.SequenceEqual(
                    fullRefProof.Refs) ||
                audit.SourceObjectSetSha256 != mirror.ObjectSetSha256 ||
                audit.SourceReachableObjectCount != mirror.ObjectCount ||
                audit.VerifiedAtUtc < mirror.VerifiedAtUtc ||
                (requireFresh && audit.VerifiedAtUtc > now.AddSeconds(5)) ||
                audit.RemoteTransportInvoked || audit.CanPush ||
                audit.CanRetry || audit.MigrationComplete)
                throw Invalid();
        }
        catch (Exception ex) when (ex is
            GitHubSourceCheckpointGitRefProofException or
            GitIncrementalAncestryException or
            GitIncrementalAncestryLedgerException or
            GitSourceSnapshotMirrorException or
            IncrementalGitRefReconciliationException)
        {
            throw Invalid();
        }
    }
}
