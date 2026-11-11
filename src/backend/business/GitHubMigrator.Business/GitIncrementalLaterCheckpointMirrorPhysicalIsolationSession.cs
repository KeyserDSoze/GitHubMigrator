namespace GitHubMigrator.Business;

// #124: server-only trust handoff from the actual F-060 POSIX volume
// lock to a source-first SQL transaction. This observation is NOT a
// migration capability: the callback must independently re-read
// the exact immutable Prepared/review/lease SQL rows before recording.
// The original lease stays RecoveryRequired in every outcome.
public sealed record GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof(
    Guid PhysicalMirrorId, Guid WorkpackId,
    string IsolationDirectoryName, string ObjectSetSha256, int ObjectCount,
    string BundleSha256, long BundleBytes,
    DateTimeOffset VerifiedAtUtc, bool RecoveredExistingQuarantine);

public interface IGitIncrementalLaterCheckpointMirrorPhysicalIsolationSession
{
    // This callback is invoked ONLY after two independently verified
    // quarantined Git graph passes, still under the same process +
    // volume-level root lock as the F-060 materializer. It is responsible
    // for a short PostgreSQL source-first transaction. No Git subprocess
    // is permitted inside that transaction.
    Task<TResult> WithVerifiedIsolationAsync<TResult>(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection expected,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        GitIncrementalLaterCheckpointMirrorIsolationIntent prepared,
        Func<GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof,
            CancellationToken, Task<TResult>> persist,
        CancellationToken ct);
}


// #129: narrow READ-ONLY interface. Deliberately cannot invoke the
// isolation session's rename operation or write a SQL receipt.
public interface IGitIncrementalLaterCheckpointMirrorPostIsolationPathAudit
{
    Task<string> InspectPreparedPathsAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
        CancellationToken ct);

    // #129: only after two full quarantined Git fsck/ref/OID passes,
    // with exact Prepared marker and bundle bytes, under the shared
    // F-060 POSIX root/process lock. Final SQL read runs INSIDE lock.
    // Unlike the isolation session this method never moves any path.
    Task<TResult> WithVerifiedQuarantineAsync<TResult>(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
        Func<CancellationToken, Task<TResult>> readSql,
        CancellationToken ct);
}
