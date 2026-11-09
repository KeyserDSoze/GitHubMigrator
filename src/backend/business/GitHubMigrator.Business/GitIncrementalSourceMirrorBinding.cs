namespace GitHubMigrator.Business;

// F-096: a purely local, read-only bridge between an F-014 source ref
// snapshot, F-035 bundle metadata and a completed F-060/F-061 physical
// workpack mirror. Never contains a filesystem path, token or GitHub URL.
public sealed record GitIncrementalSourceMirrorBinding(
    Guid MirrorId, Guid SnapshotId, Guid WorkpackId,
    Guid PlanId, Guid ReconciliationId, string SourceRepositoryId,
    string SourceEvidenceSha256, string BundleSha256,
    int BundleRefCount, string RefsSha256,
    string ObjectSetSha256, int ObjectCount,
    string MirrorProofSha256, DateTimeOffset VerifiedAtUtc,
    string State, bool RemotePushInvoked, bool RemoteWritesAuthorized,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public static class GitIncrementalSourceMirrorBindingIntegrity
{
    private static GitIncrementalAncestryLedgerException Invalid() =>
        new("GitAncestryLedgerMirrorEvidenceInvalid");

    public static void VerifyContext(
        IncrementalOperationPlan plan, VerifiedSourceGitRefs source,
        GitIncrementalSourceMirrorBinding bridge,
        DateTimeOffset currentTimeUtc, bool requireFresh)
    {
        if (plan is null || source is null || bridge is null ||
            bridge.MirrorId == Guid.Empty || bridge.WorkpackId == Guid.Empty ||
            bridge.MirrorId != GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                bridge.SnapshotId, bridge.WorkpackId) ||
            bridge.SnapshotId != plan.SnapshotId ||
            bridge.PlanId != plan.Id ||
            bridge.ReconciliationId == Guid.Empty ||
            bridge.SourceRepositoryId != source.SourceRepositoryId ||
            bridge.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
            bridge.BundleRefCount != source.Refs.Count ||
            bridge.RefsSha256 !=
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(source.Refs) ||
            bridge.State != GitSourceMirrorLifecycleStates.Verified ||
            bridge.RemotePushInvoked || bridge.RemoteWritesAuthorized ||
            bridge.MayAutomaticallyRetry || bridge.MigrationComplete ||
            bridge.VerifiedAtUtc == default ||
            (requireFresh &&
                (currentTimeUtc - bridge.VerifiedAtUtc >
                    GitSourceMirrorLifecycleIntegrity.MaxProofAge ||
                 bridge.VerifiedAtUtc > currentTimeUtc.AddSeconds(5))))
            throw Invalid();

        try
        {
            var expected = GitSourceSnapshotMirrorIntegrity.MirrorEvidenceSha(
                bridge.SnapshotId, bridge.MirrorId, bridge.WorkpackId,
                bridge.BundleSha256, bridge.SourceEvidenceSha256,
                bridge.RefsSha256, bridge.BundleRefCount,
                bridge.ObjectSetSha256, bridge.ObjectCount);
            if (bridge.MirrorProofSha256 != expected)
                throw Invalid();
        }
        catch (GitSourceSnapshotMirrorException)
        { throw Invalid(); }
    }

    public static void VerifyAudit(
        IncrementalOperationPlan plan, VerifiedSourceGitRefs source,
        GitIncrementalSourceMirrorBinding bridge,
        GitIncrementalAncestryReceipt audit,
        DateTimeOffset currentTimeUtc, bool requireFresh)
    {
        VerifyContext(plan, source, bridge, currentTimeUtc, requireFresh);
        try { GitIncrementalAncestryIntegrity.VerifyReceipt(audit); }
        catch (GitIncrementalAncestryException) { throw Invalid(); }
        if (audit.Request.SourcePhysicalMirrorId != bridge.MirrorId ||
            audit.Request.SourcePhysicalMirrorProofSha256 != bridge.MirrorProofSha256 ||
            audit.Request.SourceSnapshotId != bridge.SnapshotId ||
            !audit.Request.ExpectedSourceRefs.SequenceEqual(source.Refs) ||
            audit.SourceObjectSetSha256 != bridge.ObjectSetSha256 ||
            audit.SourceReachableObjectCount != bridge.ObjectCount ||
            audit.VerifiedAtUtc < bridge.VerifiedAtUtc ||
            (requireFresh && audit.VerifiedAtUtc >
                currentTimeUtc.AddSeconds(5)))
            throw Invalid();
    }
}
