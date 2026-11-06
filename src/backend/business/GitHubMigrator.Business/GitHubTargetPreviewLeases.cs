namespace GitHubMigrator.Business;

// F-045 coordinates only read-only destination preparation. These records
// do not authorize a GitHub push or transfer any repository objects.
public sealed record GitHubTargetPreviewLeaseClaim(
    long RepositoryId, Guid LeaseToken, long Generation,
    Guid ActorId, string PacketSha256, DateTimeOffset LeaseUntilUtc,
    string State, bool ExecutionAuthorized, bool RemoteWritesPerformed,
    bool ReadyForExecution);

public sealed record GitHubTargetPreviewLeaseStatus(
    long RepositoryId, string Organization, string Repository,
    string State, long Generation, string PacketSha256,
    DateTimeOffset? LeaseUntilUtc, DateTimeOffset UpdatedAtUtc,
    bool ExecutionAuthorized, bool RemoteWritesPerformed,
    bool ReadyForExecution);

public sealed record GitHubTargetPreviewLeaseEvent(
    long Id, long RepositoryId, long Generation, string Type,
    Guid ActorId, string PacketSha256, DateTimeOffset OccurredAtUtc);

public sealed class GitHubTargetPreviewLeaseException(string code)
    : Exception("GitHub target preview lease rejected")
{
    public string Code { get; } = code;
}

public static class GitHubTargetPreviewLeaseIntegrity
{
    public const string Held = "Held";
    public const string Released = "Released";
    public const string RecoveryRequired = "RecoveryRequired";
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(20);

    public static void VerifyPacket(GitHubCreateOnlyPacket packet)
    {
        if (packet is null || packet.RepositoryId <= 0 ||
            packet.PreparationId == Guid.Empty || packet.PlanId == Guid.Empty ||
            packet.ScopeId == Guid.Empty || packet.FenceId == Guid.Empty ||
            packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.WriteCapabilityVerified || packet.ExecutionAuthorized ||
            packet.RemoteWritesPerformed || packet.ReadyForExecution ||
            packet.CreateCandidates is < 1 or > GitImportRehearsalIntegrity.MaxCandidateRefs ||
            !GitSourceBundleStorageCheck.ValidSha(packet.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(packet.CandidateSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(packet.FenceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(packet.ScopeEvidenceSha256) ||
            string.IsNullOrWhiteSpace(packet.Organization) ||
            string.IsNullOrWhiteSpace(packet.Repository))
            throw new GitHubTargetPreviewLeaseException("PreviewLeasePacketInvalid");
    }

    public static void VerifyDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(5) || duration > MaxDuration ||
            duration.TotalSeconds != Math.Truncate(duration.TotalSeconds))
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseDurationInvalid");
    }
}

public interface IGitHubTargetPreviewLeaseStore
{
    Task<GitHubTargetPreviewLeaseClaim> AcquireAsync(
        GitHubCreateOnlyPacket packet, Guid actorId,
        TimeSpan duration, CancellationToken ct);
    Task<bool> RenewAsync(
        GitHubCreateOnlyPacket packet, Guid actorId,
        Guid leaseToken, long generation, TimeSpan duration, CancellationToken ct);
    Task<bool> ReleaseAsync(
        long repositoryId, Guid actorId, Guid leaseToken, long generation,
        string packetSha256, CancellationToken ct);
    Task<GitHubTargetPreviewLeaseStatus?> GetAsync(
        long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubTargetPreviewLeaseEvent>> EventsAsync(
        long repositoryId, CancellationToken ct);
    Task<bool> RecoverAsync(
        GitHubCreateOnlyPacket freshPacket, Guid actorId,
        long expectedGeneration, string oldPacketSha256, CancellationToken ct);
}

// A service, not the SQL repository, owns the evidence gate: clients can only
// supply F-044 scope IDs, not a self-asserted candidate packet or target ID.
public sealed class GitHubTargetPreviewLeaseService(
    GitHubCreateOnlyPacketService packets,
    IGitHubAppWriteScopeStore scopes,
    IGitHubTargetPreviewLeaseStore store,
    GitHubTargetPreviewLeaseOptions options)
{
    public async Task<GitHubTargetPreviewLeaseClaim> AcquireAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid actorId, int durationSeconds, CancellationToken ct)
    {
        EnsureEnabled();
        RequireActor(actorId);
        var duration = TimeSpan.FromSeconds(durationSeconds);
        GitHubTargetPreviewLeaseIntegrity.VerifyDuration(duration);
        var packet = await packets.PreviewAsync(preparationId, planId, scopeId, ct);
        return await store.AcquireAsync(packet, actorId, duration, ct);
    }

    public async Task<bool> RenewAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid actorId,
        Guid leaseToken, long generation, string expectedPacketSha256,
        int durationSeconds, CancellationToken ct)
    {
        EnsureEnabled();
        RequireActor(actorId);
        GitHubTargetPreviewLeaseIntegrity.VerifyDuration(
            TimeSpan.FromSeconds(durationSeconds));
        var packet = await packets.PreviewAsync(preparationId, planId, scopeId, ct);
        if (packet.EvidenceSha256 != expectedPacketSha256)
            throw new GitHubTargetPreviewLeaseException("PreviewLeasePacketChanged");
        return await store.RenewAsync(packet, actorId, leaseToken, generation,
            TimeSpan.FromSeconds(durationSeconds), ct);
    }

    public async Task<bool> ReleaseAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid actorId,
        Guid leaseToken, long generation, string expectedPacketSha256,
        CancellationToken ct)
    {
        EnsureEnabled();
        RequireActor(actorId);
        // A read-only lease can be released after the F-042 fence expires.
        // A release does NOT assert that GitHub refs still match.
        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        if (!scope.IsCurrent ||
            !GitSourceBundleStorageCheck.ValidSha(expectedPacketSha256))
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseProofInvalid");
        return await store.ReleaseAsync(scope.RepositoryId, actorId,
            leaseToken, generation, expectedPacketSha256, ct);
    }

    public async Task<GitHubTargetPreviewLeaseStatus?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        EnsureEnabled();
        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        return await store.GetAsync(scope.RepositoryId, ct);
    }

    public async Task<IReadOnlyList<GitHubTargetPreviewLeaseEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        EnsureEnabled();
        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        return await store.EventsAsync(scope.RepositoryId, ct);
    }

    public async Task<bool> RecoverAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid actorId, long expectedGeneration, string oldPacketSha256,
        string acknowledgementText, CancellationToken ct)
    {
        EnsureEnabled();
        RequireActor(actorId);
        if (acknowledgementText !=
            "I CONFIRM F045 PERFORMED NO REMOTE WRITES AND THIS IS NOT PUSH AUTHORIZATION")
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseRecoveryAcknowledgementRequired");
        // A completely new, still-fresh packet must be regenerated from
        // server evidence; no credential is recovered or retained.
        var fresh = await packets.PreviewAsync(preparationId, planId, scopeId, ct);
        return await store.RecoverAsync(fresh, actorId,
            expectedGeneration, oldPacketSha256, ct);
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseDisabled");
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseActorInvalid");
    }
}

public sealed record GitHubTargetPreviewLeaseOptions(bool Enabled);
