namespace GitHubMigrator.Business;

// F-097c.3: versioned, offline-only F-097/F-031/F-061 evidence. This
// is NOT a runnable F-094 audit request and NOT a persisted F-095
// receipt. The physical mirror still needs fresh local Git fsck.
public sealed record GitIncrementalCheckpointMirrorReadPlan(
    int EvidenceVersion,
    GitIncrementalCheckpointAuditInputs SourceAndTarget,
    GitIncrementalSourceMirrorBinding PhysicalMirror,
    bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);
