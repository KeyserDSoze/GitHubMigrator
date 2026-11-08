using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemotePostSendClosureCheckpointRecord
{
    public Guid Id { get; set; }
    public Guid BarrierId { get; set; }
    public Guid ConfirmationId { get; set; }
    public Guid IntentId { get; set; }
    public Guid HazardId { get; set; }
    public Guid GrantId { get; set; }
    public Guid ReadbackId { get; set; }
    public Guid ReconciliationId { get; set; }
    public long RepositoryId { get; set; }
    public Guid RecordedBy { get; set; }
    public string BarrierEvidenceSha256 { get; set; } = "";
    public string ReadbackEvidenceSha256 { get; set; } = "";
    public string ReconciliationEvidenceSha256 { get; set; } = "";
    public string FullObjectClosureSha256 { get; set; } = "";
    public DateTimeOffset VerifiedAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string State { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemotePostSendClosureEventRecord
{
    public long Id { get; set; }
    public Guid CheckpointId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-085 never calls GitHub and never receives a token, URL, refspec or
// original Git objects. It composes three durable independent journals:
// F-082 possible send, F-054 authenticated tips, F-058 full closure.
// A crash after F-082 always remains Pending until a NEW actor records
// independent evidence. Read is scoped to the original reviewed plan.
public sealed class PostgresGitHubRemotePostSendRecoveryJournal(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options,
    PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers)
{
    public async Task<GitHubRemotePostSendRecoveryInspection?> InspectScopedAsync(
        Guid barrierId, Guid confirmationId, Guid intentId,
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        var repo = RequireAllowed();
        var scope = await barriers.InspectScopedAsync(
            barrierId, confirmationId, intentId,
            preparationId, planId, scopeId, ct);
        if (scope is null) return null;
        return await ProjectAsync(scope.Barrier, repo, ct);
    }

    public async Task<GitHubRemotePostSendClosureCheckpoint>
        RecordIndependentClosureAsync(
        Guid barrierId, Guid confirmationId, Guid intentId,
        Guid preparationId, Guid planId, Guid scopeId,
        Guid independentObserver, CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (independentObserver == Guid.Empty)
            throw Refused("PostSendCheckpointActorInvalid");
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repo.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repo.ToString()}))", ct);

        var status = await InspectScopedAsync(barrierId, confirmationId,
            intentId, preparationId, planId, scopeId, ct) ??
            throw Refused("PostSendBarrierNotFound");
        if (status.State is not
            (GitHubRemotePostSendRecoveryStates.IndependentClosureObserved or
             GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure))
            throw Refused("PostSendIndependentClosureMissing");

        var existing = status.Checkpoint;
        if (existing is not null)
        {
            if (existing.RecordedBy != independentObserver)
                throw Refused("PostSendCheckpointConflict");
            await tx.CommitAsync(ct);
            return existing;
        }

        var marker = await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .SingleAsync(x => x.Id == barrierId && x.RepositoryId == repo, ct);
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleAsync(x => x.Id == marker.GrantId && x.RepositoryId == repo, ct);
        var readback = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleAsync(x => x.Id == status.ReadbackId && x.RepositoryId == repo, ct);
        var closure = await db.GitHubRemoteObjectReconciliations.AsNoTracking()
            .SingleAsync(x => x.Id == status.ReconciliationId &&
                x.RepositoryId == repo, ct);
        var confirmation = await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().SingleAsync(x => x.Id == confirmationId &&
                x.RepositoryId == repo, ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == grant.AttemptId && x.RepositoryId == repo, ct);
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleAsync(x => x.RepositoryId == repo, ct);
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleAsync(x => x.Id == marker.HazardId &&
                x.RepositoryId == repo, ct);
        if (new Guid?[] {
                marker.MarkedBy, confirmation.ConfirmedBy,
                confirmation.RequestedBy, grant.RequestedBy, grant.ReviewedBy,
                attempt.RequestedBy, attempt.ReviewedBy, fence.RequestedBy,
                fence.ReviewedBy, hazard.PreparedBy,
                readback.ObservedBy, closure.ObservedBy
            }.Any(actor => actor == independentObserver))
            throw Refused("PostSendIndependentObserverRequired");

        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (now < closure.VerifiedAtUtc)
            throw Refused("PostSendCheckpointClockInvalid");
        var receipt = new GitHubRemotePostSendClosureCheckpoint(
            Guid.NewGuid(), marker.Id, marker.ConfirmationId,
            marker.IntentId, marker.HazardId, marker.GrantId,
            readback.Id, closure.Id, repo, independentObserver,
            marker.EvidenceSha256, readback.JournalEvidenceSha256,
            closure.EvidenceSha256, closure.FullObjectClosureSha256,
            closure.VerifiedAtUtc, now,
            GitHubRemotePostSendRecoveryIntegrity.CheckpointState, "",
            false, false, false);
        receipt = receipt with {
            EvidenceSha256 =
                GitHubRemotePostSendRecoveryIntegrity.CheckpointDigest(receipt)
        };
        db.GitHubRemotePostSendClosureCheckpoints.Add(new()
        {
            Id = receipt.Id, BarrierId = marker.Id,
            ConfirmationId = marker.ConfirmationId,
            IntentId = marker.IntentId, HazardId = marker.HazardId,
            GrantId = marker.GrantId, ReadbackId = readback.Id,
            ReconciliationId = closure.Id, RepositoryId = repo,
            RecordedBy = receipt.RecordedBy,
            BarrierEvidenceSha256 = receipt.BarrierEvidenceSha256,
            ReadbackEvidenceSha256 = receipt.ReadbackEvidenceSha256,
            ReconciliationEvidenceSha256 = receipt.ReconciliationEvidenceSha256,
            FullObjectClosureSha256 = receipt.FullObjectClosureSha256,
            VerifiedAtUtc = receipt.VerifiedAtUtc,
            RecordedAtUtc = receipt.RecordedAtUtc,
            State = receipt.State, EvidenceSha256 = receipt.EvidenceSha256,
            CanRetry = false, CanDispatch = false, MigrationComplete = false
        });
        db.GitHubRemotePostSendClosureEvents.Add(new()
        {
            CheckpointId = receipt.Id, Type = "Recorded",
            ActorId = independentObserver, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return receipt;
    }

    private async Task<GitHubRemotePostSendRecoveryInspection> ProjectAsync(
        GitHubRemoteSandboxPreSendBarrierReceipt marker,
        long repo, CancellationToken ct)
    {
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == marker.GrantId &&
                x.RepositoryId == repo, ct);
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == marker.HazardId &&
                x.GrantId == marker.GrantId && x.RepositoryId == repo, ct);
        if (grant is null || hazard is null ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy != marker.MarkedBy ||
            hazard.UpdatedAtUtc != marker.RecordedAtUtc ||
            hazard.BaselineSha256 != grant.BaselineSha256 ||
            hazard.PacketSha256 != marker.PacketSha256 ||
            hazard.CandidateSetSha256 != marker.CandidateSetSha256 ||
            grant.PacketSha256 != marker.PacketSha256 ||
            grant.CandidateSetSha256 != marker.CandidateSetSha256)
            throw Refused("PostSendHistoryCorrupt");

        var readback = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GrantId == marker.GrantId &&
                x.RepositoryId == repo, ct);
        var closure = await db.GitHubRemoteObjectReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HazardId == marker.HazardId &&
                x.RepositoryId == repo, ct);
        if (readback is not null)
        {
            if (readback.BaselineSha256 != grant.BaselineSha256 ||
                readback.CandidateSetSha256 != marker.CandidateSetSha256 ||
                readback.ObservedAtUtc < marker.RecordedAtUtc ||
                readback.RecordedAtUtc < marker.RecordedAtUtc ||
                readback.TransportAttempted || readback.RemoteWritesAuthorized ||
                readback.MayAutomaticallyRetry ||
                !GitSourceBundleStorageCheck.ValidSha(
                    readback.JournalEvidenceSha256))
                throw Refused("PostSendReadbackHistoryCorrupt");
            var events = await db.GitHubRemoteReadbackEvents.AsNoTracking()
                .Where(x => x.ReadbackId == readback.Id)
                .Take(2).ToArrayAsync(ct);
            if (events.Length != 1 || events[0].Type != "Recorded" ||
                events[0].ActorId != readback.ObservedBy ||
                events[0].OccurredAtUtc != readback.RecordedAtUtc ||
                readback.ObservedBy == marker.MarkedBy)
                throw Refused("PostSendReadbackHistoryCorrupt");
        }

        if (closure is not null)
        {
            if (readback is null ||
                readback.State != GitHubRemoteReadbackStates.ExactTipEvidenceOnly ||
                closure.GrantId != marker.GrantId ||
                closure.ReadbackId != readback.Id ||
                closure.OriginalBaselineSha256 != grant.BaselineSha256 ||
                closure.CandidateSetSha256 != marker.CandidateSetSha256 ||
                closure.TargetRefSetSha256 != readback.RefSetSha256 ||
                closure.VerifiedAtUtc < marker.RecordedAtUtc ||
                closure.VerifiedAtUtc < readback.ObservedAtUtc ||
                closure.ObservedBy == marker.MarkedBy ||
                closure.ObservedBy == readback.ObservedBy)
                throw Refused("PostSendClosureHistoryCorrupt");
            GitHubRemotePostSendRecoveryIntegrity.VerifyFullClosure(
                MapClosure(closure));
            var events = await db.GitHubRemoteObjectReconciliationEvents
                .AsNoTracking().Where(x => x.ReconciliationId == closure.Id)
                .Take(2).ToArrayAsync(ct);
            if (events.Length != 1 || events[0].Type != "Recorded" ||
                events[0].ActorId != closure.ObservedBy ||
                events[0].OccurredAtUtc != closure.RecordedAtUtc)
                throw Refused("PostSendClosureHistoryCorrupt");
        }

        var saved = await db.GitHubRemotePostSendClosureCheckpoints
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.BarrierId == marker.Id && x.RepositoryId == repo, ct);
        GitHubRemotePostSendClosureCheckpoint? checkpoint = null;
        if (saved is not null)
        {
            checkpoint = MapCheckpoint(saved);
            GitHubRemotePostSendRecoveryIntegrity.VerifyCheckpoint(checkpoint);
            var audit = await db.GitHubRemotePostSendClosureEvents
                .AsNoTracking().Where(x => x.CheckpointId == saved.Id)
                .Take(2).ToArrayAsync(ct);
            if (closure is null || readback is null ||
                audit.Length != 1 || audit[0].Type != "Recorded" ||
                audit[0].ActorId != saved.RecordedBy ||
                audit[0].OccurredAtUtc != saved.RecordedAtUtc ||
                checkpoint.ConfirmationId != marker.ConfirmationId ||
                checkpoint.IntentId != marker.IntentId ||
                checkpoint.HazardId != marker.HazardId ||
                checkpoint.GrantId != marker.GrantId ||
                checkpoint.BarrierEvidenceSha256 != marker.EvidenceSha256 ||
                checkpoint.ReadbackId != readback.Id ||
                checkpoint.ReadbackEvidenceSha256 != readback.JournalEvidenceSha256 ||
                checkpoint.ReconciliationId != closure.Id ||
                checkpoint.ReconciliationEvidenceSha256 != closure.EvidenceSha256 ||
                checkpoint.FullObjectClosureSha256 != closure.FullObjectClosureSha256 ||
                checkpoint.VerifiedAtUtc != closure.VerifiedAtUtc)
                throw Refused("PostSendCheckpointHistoryCorrupt");
        }

        var state = checkpoint is not null
            ? GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure
            : closure is not null
                ? GitHubRemotePostSendRecoveryStates.IndependentClosureObserved
                : readback is null
                    ? GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback
                    : readback.State == GitHubRemoteReadbackStates.ExactTipEvidenceOnly
                        ? GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure
                        : GitHubRemotePostSendRecoveryStates.OperatorReviewRequired;
        return new(marker.Id, marker.ConfirmationId, marker.IntentId,
            marker.HazardId, marker.GrantId, repo, state,
            marker.EvidenceSha256, readback?.Id,
            readback?.JournalEvidenceSha256, closure?.Id,
            closure?.EvidenceSha256, closure?.FullObjectClosureSha256,
            checkpoint, false, false, false);
    }

    private static GitHubRemoteObjectReconciliationStatus MapClosure(
        GitHubRemoteObjectReconciliationRecord s) =>
        new(s.Id, s.GrantId, s.ReadbackId, s.HazardId, s.RepositoryId,
            s.SourceSnapshotId, s.State, s.OriginalBaselineSha256,
            s.CandidateSetSha256, s.TargetRefSetSha256,
            s.RemoteRefObservationSha256, s.FullObjectClosureSha256,
            s.RemoteMirrorReceiptSha256, s.EvidenceSha256, s.ObservedBy,
            s.VerifiedAtUtc, s.RecordedAtUtc, s.RemotePushInvoked,
            s.RemoteWritesAuthorized, s.MayAutomaticallyRetry,
            s.MigrationComplete);

    private static GitHubRemotePostSendClosureCheckpoint MapCheckpoint(
        GitHubRemotePostSendClosureCheckpointRecord x) =>
        new(x.Id, x.BarrierId, x.ConfirmationId, x.IntentId,
            x.HazardId, x.GrantId, x.ReadbackId, x.ReconciliationId,
            x.RepositoryId, x.RecordedBy, x.BarrierEvidenceSha256,
            x.ReadbackEvidenceSha256, x.ReconciliationEvidenceSha256,
            x.FullObjectClosureSha256, x.VerifiedAtUtc, x.RecordedAtUtc,
            x.State, x.EvidenceSha256, x.CanRetry, x.CanDispatch,
            x.MigrationComplete);

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("PostSendRecoveryDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemotePostSendRecoveryException Refused(string code) =>
        new(code);
}
