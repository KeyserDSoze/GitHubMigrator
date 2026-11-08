using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-087: one special post-send F-054 writer. Legacy pre-send F-054 is
// unchanged. Grant/fence deadlines are intentionally NOT execution
// authority after F-082; only a verified immutable F-086 snapshot and
// irreversible F-082 marker can authorize this READ-ONLY observation.
public sealed class PostgresGitHubRemoteRecoveredReadbackJournal(
    AppDbContext db, TimeProvider clock,
    GitHubRemoteTestGrantOptions options,
    IGitHubRemoteTypedRecoveryBaselineJournal baselines,
    PostgresGitHubRemotePostSendRecoveryJournal recovery)
    : IGitHubRemoteRecoveredReadbackJournal
{
    public async Task<GitHubRemoteReadbackStatus> RecordRecoveredAsync(
        GitHubRemoteTypedRecoveryBaselineReceipt typed,
        GitHubRemoteReadbackEvidence evidence, CancellationToken ct)
    {
        if (typed is null || evidence is null ||
            !options.Enabled || options.AllowedRepositoryId <= 0 ||
            typed.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(options.AllowedRepository,
                typed.Organization + "/" + typed.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw Refused("RecoveredReadbackDisabledOrWrongTarget");
        var decision = GitHubRemoteReadbackIntegrity.Verify(
            evidence, clock.GetUtcNow());
        if (evidence.RepositoryId != typed.RepositoryId ||
            evidence.GrantId != typed.GrantId ||
            evidence.ObserverId == Guid.Empty ||
            evidence.ObserverId == typed.CapturedBy ||
            !evidence.Baseline.SequenceEqual(typed.Baseline.Refs) ||
            !evidence.CreateCandidates.SequenceEqual(typed.CreateCandidates) ||
            decision.BaselineSha256 != typed.BaselineSha256 ||
            decision.CandidateSetSha256 != typed.CandidateSetSha256 ||
            evidence.Snapshot.RemoteRefs.ObservedAtUtc < typed.RecordedAtUtc)
            throw Refused("RecoveredReadbackSnapshotMismatch");
        GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(typed);

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({typed.RepositoryId.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({typed.RepositoryId.ToString()}))", ct);

        // Re-read the F-086 row after acquiring the same two locks as
        // F-082; reject tampered or merely pre-saved-but-not-sent refs.
        var stored = await baselines.ReadAfterBarrierAsync(
            typed.ConfirmationId, typed.IntentId, typed.PreparationId,
            typed.PlanId, typed.ScopeId, ct);
        if (stored is null ||
            stored.Id != typed.Id ||
            stored.EvidenceSha256 != typed.EvidenceSha256)
            throw Refused("RecoveredReadbackHistoricalProofMissing");

        var barrier = await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConfirmationId == typed.ConfirmationId &&
                x.HazardId == typed.HazardId &&
                x.GrantId == typed.GrantId &&
                x.RepositoryId == typed.RepositoryId, ct);
        if (barrier is null)
            throw Refused("RecoveredReadbackBarrierMissing");
        var state = await recovery.InspectScopedAsync(
            barrier.Id, typed.ConfirmationId, typed.IntentId,
            typed.PreparationId, typed.PlanId, typed.ScopeId, ct);
        if (state is null ||
            state.BarrierEvidenceSha256 != barrier.EvidenceSha256)
            throw Refused("RecoveredReadbackBarrierMissing");
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleAsync(x => x.Id == typed.GrantId &&
                x.RepositoryId == typed.RepositoryId, ct);
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleAsync(x => x.Id == typed.HazardId &&
                x.RepositoryId == typed.RepositoryId, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == typed.RepositoryId, ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId &&
                x.RepositoryId == typed.RepositoryId, ct);
        var confirmation = await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().SingleAsync(x => x.Id == typed.ConfirmationId &&
                x.RepositoryId == typed.RepositoryId, ct);

        // Expiry is deliberately allowed for history, but it CANNOT
        // grant execution. Every original write-authority flag remains
        // false and all independent actors must be disjoint.
        if (grant.State is not
                (GitHubRemoteTestGrantStates.ReviewedWriteDisabled or
                 GitHubRemoteTestGrantStates.RecoveryRequired) ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy != typed.CapturedBy ||
            grant.BaselineSha256 != typed.BaselineSha256 ||
            grant.PacketSha256 != typed.PacketSha256 ||
            grant.CandidateSetSha256 != typed.CandidateSetSha256 ||
            hazard.PacketSha256 != typed.PacketSha256 ||
            hazard.CandidateSetSha256 != typed.CandidateSetSha256 ||
            hazard.BaselineSha256 != typed.BaselineSha256 ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            fence is null || attempt is null ||
            fence.State is not
                (GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled or
                 GitHubRemoteSandboxFenceStates.RecoveryRequired) ||
            fence.RequestId != grant.FenceRequestId ||
            fence.Generation != grant.FenceGeneration ||
            fence.AttemptId != grant.AttemptId ||
            fence.PacketSha256 != typed.PacketSha256 ||
            fence.CandidateSetSha256 != typed.CandidateSetSha256 ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.PacketSha256 != typed.PacketSha256 ||
            attempt.CandidateSetSha256 != typed.CandidateSetSha256 ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            new Guid?[] {
                typed.CapturedBy, hazard.PreparedBy, grant.RequestedBy,
                grant.ReviewedBy, fence.RequestedBy, fence.ReviewedBy,
                attempt.RequestedBy, attempt.ReviewedBy,
                confirmation.RequestedBy, confirmation.ConfirmedBy
            }.Any(actor => actor is null || actor == Guid.Empty ||
                actor == evidence.ObserverId))
            throw Refused("RecoveredReadbackAuthorityChanged");

        var existing = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GrantId == typed.GrantId &&
                x.RepositoryId == typed.RepositoryId, ct);
        if (existing is not null)
        {
            if (existing.JournalEvidenceSha256 != decision.JournalEvidenceSha256 ||
                existing.ObservedBy != evidence.ObserverId)
                throw Refused("RecoveredReadbackConflict");
            await tx.CommitAsync(ct);
            return Map(existing);
        }

        var observedAt = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (decision.ObservedAtUtc < barrier.RecordedAtUtc ||
            observedAt < decision.ObservedAtUtc ||
            observedAt - decision.ObservedAtUtc >
                GitHubRemoteReadbackIntegrity.MaxObservationAge)
            throw Refused("RecoveredReadbackObservationOutsideEpoch");
        var receipt = new GitHubRemoteReadbackRecord
        {
            Id = Guid.NewGuid(), GrantId = typed.GrantId,
            RepositoryId = typed.RepositoryId,
            State = decision.State,
            BaselineSha256 = decision.BaselineSha256,
            CandidateSetSha256 = decision.CandidateSetSha256,
            RefSetSha256 = decision.RefSetSha256,
            TipEvidenceSha256 = decision.TipEvidenceSha256,
            AssessmentSha256 = decision.AssessmentSha256,
            JournalEvidenceSha256 = decision.JournalEvidenceSha256,
            ObservedBy = evidence.ObserverId,
            ObservedAtUtc = decision.ObservedAtUtc,
            RecordedAtUtc = observedAt,
            TransportAttempted = false, RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false
        };
        db.GitHubRemoteReadbacks.Add(receipt);
        db.GitHubRemoteReadbackEvents.Add(new()
        {
            ReadbackId = receipt.Id, Type = "Recorded",
            ActorId = evidence.ObserverId, OccurredAtUtc = observedAt
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(receipt);
    }

    private static GitHubRemoteReadbackStatus Map(
        GitHubRemoteReadbackRecord x) =>
        new(x.Id, x.GrantId, x.RepositoryId, x.State,
            x.BaselineSha256, x.CandidateSetSha256,
            x.RefSetSha256, x.TipEvidenceSha256,
            x.AssessmentSha256, x.JournalEvidenceSha256, x.ObservedBy,
            x.ObservedAtUtc, x.RecordedAtUtc, x.TransportAttempted,
            x.RemoteWritesAuthorized, x.MayAutomaticallyRetry);

    private static GitHubRemoteReadbackException Refused(string code) =>
        new(code);
}
