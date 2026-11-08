namespace GitHubMigrator.Business;

// F-088: read-only operator summary of F-082 / F-054 / F-058 / F-085.
// A verified full object closure is *evidence*, not proof of causality
// or a migration-complete transition. No DTO can authorize a write.
public sealed record GitHubRemoteRecoveryWorkbenchOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed class GitHubRemoteRecoveryWorkbenchException(string code)
    : Exception("Remote recovery workbench refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteRecoveryWorkbenchActions
{
    public const string ObserveTips = "ObserveTipsReadOnly";
    public const string VerifyFullObjects = "VerifyFullObjectsReadOnly";
    public const string CertifyClosure = "CertifyIndependentClosure";
    public const string OperatorReview = "OperatorReviewRequired";
    public const string None = "None";
}

public sealed record GitHubRemoteRecoveryWorkbenchItem(
    Guid BarrierId, Guid ConfirmationId, Guid IntentId, Guid SessionId,
    Guid PreparationId, Guid PlanId, Guid ScopeId, Guid HazardId,
    Guid GrantId, long RepositoryId, string Organization, string Repository,
    DateTimeOffset PossibleSendRecordedAtUtc, string State,
    string RecommendedNextAction, bool NeedsOperatorAttention,
    bool TypedBaselineAvailable, int? BaselineRefCount,
    int? CreateCandidateCount,
    string BarrierEvidenceSha256, string? TypedBaselineEvidenceSha256,
    Guid? ReadbackId, string? ReadbackEvidenceSha256,
    Guid? ReconciliationId, string? ReconciliationEvidenceSha256,
    string? FullObjectClosureSha256, Guid? ClosureCheckpointId,
    string? ClosureCheckpointEvidenceSha256,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed record GitHubRemoteRecoveryWorkbenchPage(
    Guid PreparationId, Guid PlanId, long RepositoryId,
    IReadOnlyList<GitHubRemoteRecoveryWorkbenchItem> Items,
    Guid? NextAfter, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

// Projection-only interface: multi-repo dashboards can consult the
// separately allowlisted sandbox without depending on its full F-081
// PostgreSQL service graph or granting any new remote authority.
public interface IGitHubRemoteRecoveryHistoryReader
{
    Task<GitHubRemoteRecoveryWorkbenchPage> ListAsync(
        Guid preparationId, Guid planId, Guid? after, int take,
        CancellationToken ct);
}

public static class GitHubRemoteRecoveryWorkbenchIntegrity
{
    public static string NextAction(string state, bool hasTyped)
    {
        if (state is null) throw Invalid();
        if (state == GitHubRemotePostSendRecoveryStates.OperatorReviewRequired)
            return GitHubRemoteRecoveryWorkbenchActions.OperatorReview;
        if (state == GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure)
            return GitHubRemoteRecoveryWorkbenchActions.None;
        if (state == GitHubRemotePostSendRecoveryStates.IndependentClosureObserved)
            return GitHubRemoteRecoveryWorkbenchActions.CertifyClosure;
        if (!hasTyped)
            return GitHubRemoteRecoveryWorkbenchActions.OperatorReview;
        if (state == GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback)
            return GitHubRemoteRecoveryWorkbenchActions.ObserveTips;
        if (state == GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure)
            return GitHubRemoteRecoveryWorkbenchActions.VerifyFullObjects;
        throw Invalid();
    }

    public static void RequireSafe(GitHubRemoteRecoveryWorkbenchItem item)
    {
        if (item.BarrierId == Guid.Empty || item.ConfirmationId == Guid.Empty ||
            item.IntentId == Guid.Empty || item.ScopeId == Guid.Empty ||
            item.GrantId == Guid.Empty || item.HazardId == Guid.Empty ||
            item.RepositoryId <= 0 || item.PossibleSendRecordedAtUtc == default ||
            item.CanPush || item.CanRetry || item.CanDispatch ||
            item.MigrationComplete ||
            item.RecommendedNextAction != NextAction(
                item.State, item.TypedBaselineAvailable))
            throw Invalid();
        if (item.State == GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure
            && (item.ClosureCheckpointId is null ||
                !GitSourceBundleStorageCheck.ValidSha(
                    item.ClosureCheckpointEvidenceSha256)))
            throw Invalid();
        if (item.State == GitHubRemotePostSendRecoveryStates.IndependentClosureObserved
            && (item.ReconciliationId is null ||
                !GitSourceBundleStorageCheck.ValidSha(
                    item.FullObjectClosureSha256)))
            throw Invalid();
    }

    private static GitHubRemoteRecoveryWorkbenchException Invalid() =>
        new("RecoveryWorkbenchHistoryInvalid");
}
