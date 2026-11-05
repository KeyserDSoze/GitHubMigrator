using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-029 is an immutable *operation specification*, not an executable queue.
// Operation kinds are descriptive; no command payload, target writer, OAuth
// scope or delegated permission exists anywhere in this contract.
public sealed record IncrementalOperationStep(
    int Ordinal, int? DependsOnOrdinal, string SourceRepositoryId,
    string TargetRepositoryName, string Kind, string State, string BlockerCode,
    int ChangedRefs);

public sealed record IncrementalOperationPlan(
    Guid Id, Guid PreparationId, Guid SnapshotId, Guid SourceConnectionId,
    Guid DestinationConnectionId, string Organization,
    string ReviewSha256, string PreparationEvidenceSha256,
    string PreflightEvidenceSha256, string ManifestSha256,
    DateTimeOffset CreatedAtUtc, Guid CreatedBy,
    int RepositoryCount, int StepCount, int ChangedRefCount,
    int SensitiveChangeCount, bool IsCurrent,
    bool ReadyForExecution, IReadOnlyList<string> Blockers,
    IReadOnlyList<IncrementalOperationStep> Steps);

public sealed record IncrementalExecutionAuthorizationAttempt(
    long Id, Guid PlanId, Guid ActorId, DateTimeOffset AtUtc,
    string ManifestSha256, string Decision, string ReasonCode);

public sealed class IncrementalOperationPlanException(string code)
    : Exception("Incremental operational plan rejected")
{
    public string Code { get; } = code;
}

public static class IncrementalOperationPlanIntegrity
{
    // Operation nodes stay blocked until a *future, separately implemented*
    // capability verifier and execution authorization system exist.
    public static IReadOnlyList<IncrementalOperationStep> BuildSteps(
        IReadOnlyList<IncrementalTargetProbe> targetMappings,
        IReadOnlyDictionary<string, int> changedRefs)
    {
        if (targetMappings.Count is < 1 or > 10 ||
            targetMappings.Select(x => x.SourceRepositoryId)
                .Distinct(StringComparer.Ordinal).Count() != targetMappings.Count ||
            targetMappings.Select(x => x.TargetRepositoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetMappings.Count)
            throw new IncrementalOperationPlanException("OperationMappingInvalid");
        if (targetMappings.Any(x => x.HistoryStrategy != "GitMirror"))
            throw new IncrementalOperationPlanException("OperationHistoryStrategyUnsupported");

        var steps = new List<IncrementalOperationStep>(targetMappings.Count * 6);
        foreach (var repo in targetMappings.OrderBy(x => x.SourceRepositoryId,
                     StringComparer.Ordinal))
        {
            var changed = changedRefs.GetValueOrDefault(repo.SourceRepositoryId);
            if (changed < 0) throw new IncrementalOperationPlanException("InvalidRefCount");
            Append("ReviewTargetName", "TargetAvailabilityUnverified");
            Append("ReviewSecurityMapping", "EffectivePermissionsUnverified");
            Append("TransferGitHistory", "DestinationWritePermissionUnverified");
            Append("ReconcileGitRefs", "DestinationWritePermissionUnverified");
            Append("VerifyTransferredRefs", "ExecutionNotAuthorized");
            Append("FinalizeMigrationAudit", "ExecutionNotAuthorized");

            void Append(string kind, string blocker)
            {
                var ordinal = steps.Count + 1;
                steps.Add(new(ordinal, ordinal % 6 == 1 ? null : ordinal - 1,
                    repo.SourceRepositoryId, repo.TargetRepositoryName,
                    kind, "Blocked", blocker, changed));
            }
        }
        return steps;
    }

    // Stable across workers, DB roundtrips and repeated API requests.
    // Includes the F-025/F-026 evidence, F-027 approval and F-028 live
    // observation digest, as well as the complete ordered operation DAG.
    public static string Hash(
        Guid preparationId, Guid snapshotId, Guid sourceConnectionId,
        Guid destinationConnectionId, string organization,
        string reviewSha256, string preparationSha256, string preflightSha256,
        int sensitiveCount, IReadOnlyList<IncrementalOperationStep> steps)
    {
        var payload = JsonSerializer.Serialize(new object?[] {
            "incremental-operation-manifest-v1",
            preparationId.ToString("D"), snapshotId.ToString("D"),
            sourceConnectionId.ToString("D"), destinationConnectionId.ToString("D"),
            organization, reviewSha256, preparationSha256, preflightSha256,
            sensitiveCount,
            steps.OrderBy(x => x.Ordinal).Select(x => new object?[] {
                x.Ordinal, x.DependsOnOrdinal, x.SourceRepositoryId,
                x.TargetRepositoryName, x.Kind, x.State, x.BlockerCode,
                x.ChangedRefs
            }).ToArray()
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static IReadOnlyList<string> Blockers(
        IncrementalTargetPreflightView target, int sensitiveChangeCount)
    {
        var reasons = new List<string>();
        if (target.VisibleCollisions > 0) reasons.Add("TargetNameCollision");
        if (!target.TargetAvailabilityVerified)
            reasons.Add("TargetAvailabilityUnverified");
        if (!target.RepositoryCreatePermissionVerified)
            reasons.Add("DestinationWritePermissionUnverified");
        if (!target.MembershipVerified) reasons.Add("MembershipNotVerified");
        if (sensitiveChangeCount > 0) reasons.Add("SecurityReviewRequired");
        // These are not grantable by an F-029 API; no executor exists.
        reasons.Add("ExecutionAdapterNotImplemented");
        return reasons;
    }
}

public interface IIncrementalOperationPlanRepository
{
    Task<IncrementalOperationPlan> CreateAsync(
        Guid preparationId, Guid actorId, string expectedPreflightSha256,
        CancellationToken ct);
    Task<IncrementalOperationPlan?> GetAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
    Task<IReadOnlyList<IncrementalOperationPlan>> ListAsync(
        Guid preparationId, CancellationToken ct);
    Task<IncrementalExecutionAuthorizationAttempt> RequestAuthorizationAsync(
        Guid preparationId, Guid planId, Guid actorId,
        string expectedManifestSha256, bool acknowledgedRisks,
        CancellationToken ct);
    Task<IReadOnlyList<IncrementalExecutionAuthorizationAttempt>> EventsAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
}

public sealed class IncrementalOperationPlanService(IIncrementalOperationPlanRepository repository)
{
    private static bool ValidSha(string? sha) =>
        sha is { Length: 64 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public Task<IncrementalOperationPlan> CreateAsync(
        Guid preparationId, Guid actorId, string expectedPreflightSha256,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            !ValidSha(expectedPreflightSha256))
            throw new IncrementalOperationPlanException("InvalidOperationPlanRequest");
        return repository.CreateAsync(preparationId, actorId, expectedPreflightSha256, ct);
    }

    public Task<IncrementalOperationPlan?> GetAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        repository.GetAsync(preparationId, planId, ct);

    public Task<IReadOnlyList<IncrementalOperationPlan>> ListAsync(
        Guid preparationId, CancellationToken ct) =>
        repository.ListAsync(preparationId, ct);

    public Task<IncrementalExecutionAuthorizationAttempt> RequestAuthorizationAsync(
        Guid preparationId, Guid planId, Guid actorId,
        string expectedManifestSha256, bool acknowledgedRisks,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            actorId == Guid.Empty || !ValidSha(expectedManifestSha256))
            throw new IncrementalOperationPlanException("InvalidExecutionAuthorizationRequest");
        if (!acknowledgedRisks)
            throw new IncrementalOperationPlanException("ExplicitRiskAcknowledgmentRequired");
        return repository.RequestAuthorizationAsync(preparationId, planId, actorId,
            expectedManifestSha256, acknowledgedRisks, ct);
    }

    public Task<IReadOnlyList<IncrementalExecutionAuthorizationAttempt>> EventsAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        repository.EventsAsync(preparationId, planId, ct);
}
