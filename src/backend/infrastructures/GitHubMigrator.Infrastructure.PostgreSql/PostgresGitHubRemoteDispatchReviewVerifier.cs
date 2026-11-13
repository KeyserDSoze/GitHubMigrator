using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-075: purely READ-ONLY PostgreSQL authority check for the F-074
// write-disabled review. No production DI registration, HTTP endpoint,
// remote Git sender, capability token or migration-complete transition.
// F-045 uses lock family 732911, F-050/F-052/F-055 family 733050.
// Acquire in this order to fence both concurrent mutation families.
public sealed class PostgresGitHubRemoteDispatchReviewVerifier(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options)
    : IGitHubRemoteDispatchAuthorityVerifier
{
    public async Task<GitHubRemoteCreateOnlyDispatchReview> VerifyAsync(
        GitHubCreateOnlyCandidatePacket source,
        GitHubRemoteAppPreflightReceipt preflight,
        GitHubRefObservation latest, Guid hazardId,
        Guid independentActor, CancellationToken ct)
    {
        if (source?.Packet is null || preflight is null ||
            latest is null || hazardId == Guid.Empty ||
            independentActor == Guid.Empty)
            throw Refused();
        var packet = source.Packet;
        var repoId = packet.RepositoryId;
        try
        {
            GitHubRemoteTransmitHazardIntegrity.RequireAllowed(options, repoId);
        }
        catch (GitHubRemoteTransmitHazardException)
        {
            throw Refused();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Both lock families protect the exact numeric destination ID,
        // never a mutable org/repository slug. No transaction timeout or
        // ambient host clock determines when a lease has expired.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repoId.ToString()}))",
            ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repoId.ToString()}))",
            ct);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == hazardId &&
                x.RepositoryId == repoId, ct);
        if (hazard is null)
            throw Refused();
        // Map the irreversible F-055 possible-send and operator-recovery
        // states to a stable PostgreSQL-authority failure, before invoking
        // F-074's more general in-memory proof validator.
        if (hazard.State !=
                GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.MarkedBy is not null || hazard.DeadlineUtc <= now ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry)
            throw Refused();
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == hazard.GrantId &&
                x.RepositoryId == repoId, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repoId, ct);
        if (grant is null || fence is null)
            throw Refused();
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId &&
                x.RepositoryId == repoId, ct);
        var lease = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repoId, ct);
        if (attempt is null || lease is null)
            throw Refused();

        // Check the independent F-045 lease (same token, epoch, actor,
        // packet, repo) and all four physical write-disabled flags.
        // Do not infer lease authority solely from F-047/F-052 DTOs.
        if (lease.State != GitHubTargetPreviewLeaseIntegrity.Held ||
            lease.LeaseUntilUtc is null || lease.LeaseUntilUtc <= now ||
            lease.LeaseToken != attempt.LeaseToken ||
            lease.Generation != attempt.LeaseGeneration ||
            lease.ActorId != attempt.RequestedBy ||
            lease.PacketSha256 != attempt.PacketSha256 ||
            !SameTarget(lease.Organization, lease.Repository,
                packet.Organization, packet.Repository) ||
            lease.ExecutionAuthorized || lease.RemoteWritesPerformed ||
            lease.ReadyForExecution)
            throw Refused();

        // The immutable event log must attest the currently selected
        // grant/hazard generation; a forged row alone isn't sufficient.
        var grantEvents = await db.GitHubRemoteTestGrantEvents.AsNoTracking()
            .Where(x => x.GrantId == grant.Id)
            .OrderBy(x => x.Id).Select(x => x.Type).Take(4)
            .ToArrayAsync(ct);
        var hazardEvents = await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == hazard.Id)
            .OrderBy(x => x.Id).Select(x => x.Type).Take(3)
            .ToArrayAsync(ct);
        if (!grantEvents.SequenceEqual(new[] {
                GitHubRemoteTestGrantStates.Proposed,
                GitHubRemoteTestGrantStates.ReviewedWriteDisabled }) ||
            !hazardEvents.SequenceEqual(new[] {
                GitHubRemoteTransmitHazardStates.PreparedWriteDisabled }))
            throw Refused();

        var g = new GitHubRemoteTestGrantStatus(
            grant.Id, grant.RepositoryId, grant.Organization, grant.Repository,
            grant.PreparationId, grant.PlanId, grant.ScopeId, grant.AttemptId,
            grant.FenceRequestId, grant.FenceGeneration, grant.PacketSha256,
            grant.CandidateSetSha256, grant.OriginalPolicySha256,
            grant.BaselineSha256, grant.PreflightSha256,
            grant.State, grant.RequestedBy, grant.ReviewedBy,
            grant.DeadlineUtc, grant.UpdatedAtUtc,
            grant.ExecutionAuthorized, grant.RemoteWritesPerformed,
            grant.ReadyForExecution);
        var f = new GitHubRemoteSandboxFenceStatus(
            fence.RequestId, fence.RepositoryId, fence.Organization,
            fence.Repository, fence.PreparationId, fence.PlanId,
            fence.ScopeId, fence.AttemptId, fence.PacketSha256,
            fence.CandidateSetSha256, fence.PolicySha256, fence.Generation,
            fence.State, fence.RequestedBy, fence.ReviewedBy,
            fence.DeadlineUtc, fence.UpdatedAtUtc, fence.ExecutionAuthorized,
            fence.RemoteWritesPerformed, fence.ReadyForExecution);
        var a = new GitHubTransferAttemptView(
            attempt.Id, attempt.PreparationId, attempt.PlanId, attempt.ScopeId,
            attempt.RepositoryId, attempt.Organization, attempt.Repository,
            attempt.PacketSha256, attempt.PolicySha256,
            attempt.CandidateSetSha256, attempt.CandidateCount,
            attempt.LeaseGeneration, attempt.State,
            attempt.RequestedBy, attempt.ReviewedBy, attempt.CreatedAtUtc,
            attempt.ReviewedAtUtc, attempt.BlockedAtUtc,
            attempt.ExpiredAtUtc, attempt.TransportAttempted,
            attempt.ExecutionAuthorized, attempt.RemoteWritesPerformed,
            attempt.ReadyForExecution);
        var h = new GitHubRemoteTransmitHazardStatus(
            hazard.Id, hazard.GrantId, hazard.RepositoryId,
            hazard.FenceRequestId, hazard.FenceGeneration,
            hazard.PacketSha256, hazard.CandidateSetSha256,
            hazard.BaselineSha256, hazard.State, hazard.PreparedBy,
            hazard.MarkedBy, hazard.DeadlineUtc, hazard.UpdatedAtUtc,
            hazard.TransportAuthorized, hazard.TransportInvoked,
            hazard.RemoteWritesPerformed, hazard.MayAutomaticallyRetry);

        try
        {
            // Reuse F-055's full cross-record business policy, including
            // independent operators and the previous no-write barriers.
            GitHubRemoteTransmitHazardIntegrity.RequireDependencies(
                g, f, a, options, h.PreparedBy, now);
        }
        catch (GitHubRemoteTransmitHazardException)
        {
            throw Refused();
        }
        if (f.RequestId != preflight.RequestId ||
            f.Generation != preflight.Generation ||
            f.PacketSha256 != packet.EvidenceSha256 ||
            f.CandidateSetSha256 != packet.CandidateSetSha256 ||
            f.PolicySha256 != preflight.OriginalPolicySha256 ||
            f.PreparationId != packet.PreparationId ||
            f.PlanId != packet.PlanId || f.ScopeId != packet.ScopeId ||
            f.AttemptId != a.Id ||
            a.CandidateCount != packet.CreateCandidates ||
            a.PolicySha256 != preflight.OriginalPolicySha256 ||
            a.LeaseGeneration != lease.Generation ||
            a.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            a.TransportAttempted || a.ExecutionAuthorized ||
            a.RemoteWritesPerformed || a.ReadyForExecution ||
            h.PreparedBy == independentActor ||
            f.RequestedBy == independentActor ||
            f.ReviewedBy == independentActor ||
            a.RequestedBy == independentActor ||
            a.ReviewedBy == independentActor)
            throw Refused();

        // The F-074 pure integrity computation is performed while the
        // PostgreSQL advisory locks still fence all authority rows.
        // F-068's database time avoids inter-replica skew.
        var receipt = GitHubRemoteCreateOnlyDispatchIntegrity.Seal(
            source, preflight, g, h, latest, independentActor, now);
        await transaction.CommitAsync(ct);
        return receipt;
    }

    private static bool SameTarget(
        string aOrg, string aRepo, string bOrg, string bRepo) =>
        string.Equals(aOrg, bOrg, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(aRepo, bRepo, StringComparison.OrdinalIgnoreCase);

    private static GitHubRemoteCreateOnlyDispatchException Refused() =>
        new("RemoteDispatchPersistedAuthorityChanged");
}
