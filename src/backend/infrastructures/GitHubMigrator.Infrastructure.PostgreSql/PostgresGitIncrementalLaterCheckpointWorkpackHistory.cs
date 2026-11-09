using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #111: historical integrity only. This read path never calls the writer,
// obtains GitHub credentials, retries a transfer, or grants a current gate.
public sealed record GitIncrementalLaterCheckpointHistoryOptions(bool Enabled);

public sealed record GitIncrementalLaterCheckpointHistory(
    int EvidenceVersion, Guid PreparationId, Guid PlanId, Guid CheckpointId,
    Guid SourceSnapshotId, Guid TargetObservationId, Guid ReconciliationId,
    Guid WorkpackId, string EvidenceSha256, string ReconciliationSha256,
    string SourceFullRefProofSha256, string TargetReceiptSha256,
    DateTimeOffset RecordedAtUtc, int SourceRefCount, int TargetRefCount,
    int MatchingCount, int CreateCandidateCount, int DivergedCount,
    int TargetOnlyCount, string State, string Phase, bool HistoricalOnly,
    bool CurrentApprovalVerified, bool ReadyForExecution, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed class GitIncrementalLaterCheckpointHistoryException(string code)
    : Exception("Checkpoint workpack history rejected")
{
    public string Code { get; } = code;
}

public sealed class PostgresGitIncrementalLaterCheckpointWorkpackHistory(
    AppDbContext db,
    GitIncrementalLaterCheckpointHistoryOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    PostgresGitHubSourceCheckpointGitRefLedger sourceLedger,
    PostgresGitCheckpointTargetObservationJournal targetJournal)
{
    private static GitIncrementalLaterCheckpointHistoryException Refused(
        string code) => new(code);

    private static bool Sha(string? s) => s is { Length: 64 } &&
        s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitIncrementalLaterCheckpointHistory?> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId, Guid workpackId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointHistoryDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || workpackId == Guid.Empty ||
            targetObservationId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !Sha(expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointHistoryInvalidRequest");

        var saved = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workpackId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetObservationId == targetObservationId, ct);
        if (saved is null)
            return null;

        // Do not substitute an historical F-029 target, an untrusted
        // source fingerprint, or caller-supplied ref/OID arrays.
        IncrementalOperationPlan? plan;
        GitHubSourceSyncCheckpointReceipt? checkpoint;
        GitHubSourceCheckpointGitRefProof? source;
        GitCheckpointTargetObservation? target;
        try
        {
            plan = await plans.GetAsync(preparationId, planId, ct);
            checkpoint = await checkpoints.GetVerifiedAsync(
                preparationId, planId, checkpointId, ct);
            source = await sourceLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
            target = await targetJournal.ReadHistoricalAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (Exception ex) when (ex is GitHubSourceSyncCheckpointException
            or GitHubSourceCheckpointGitRefLedgerException
            or GitCheckpointTargetObservationException)
        { throw Refused("LaterCheckpointHistoryProvenanceCorrupt"); }

        if (plan is null || checkpoint is null || source is null ||
            target is null || target.Id != targetObservationId ||
            target.EvidenceSha256 != expectedTargetReceiptSha256)
            throw Refused("LaterCheckpointHistoryProvenanceChanged");

        GitIncrementalCheckpointAuditInputs selected;
        GitMirrorRefReconciliation compared;
        try
        {
            selected = GitIncrementalCheckpointTargetJoin.ForReadOnlyComparison(
                plan, checkpoint, source, target);
            compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                selected.TargetEvidenceSha256,
                selected.ExpectedSourceRefs, selected.ExpectedTargetRefs));
        }
        catch (Exception ex) when (ex is
            GitIncrementalCheckpointPreflightException or
            GitMirrorRefReconciliationException)
        { throw Refused("LaterCheckpointHistoryProvenanceCorrupt"); }

        GitIncrementalLaterCheckpointWorkpackProof receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<
                GitIncrementalLaterCheckpointWorkpackProof>(saved.ReceiptJson)
                ?? throw new JsonException();
            GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                receipt, plan, selected, source, compared);
        }
        catch (Exception ex) when (ex is JsonException or
            GitIncrementalCheckpointWorkpackException)
        { throw Refused("LaterCheckpointHistoryReceiptCorrupt"); }

        var recon = await db.GitLaterCheckpointReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == saved.ReconciliationId, ct);
        var evs = await db.GitLaterCheckpointWorkpackEvents.AsNoTracking()
            .Where(x => x.WorkpackId == saved.Id).Take(2).ToArrayAsync(ct);
        var sourceRow = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x => x.CheckpointId ==
                checkpointId && x.SourceRepositoryId == sourceRepositoryId, ct);
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var humanEvents = human is null
            ? Array.Empty<GitCheckpointTargetHumanReviewEventRecord>()
            : await db.GitCheckpointTargetHumanReviewEvents.AsNoTracking()
                .Where(x => x.DecisionId == human.Id).Take(2).ToArrayAsync(ct);

        if (receipt.WorkpackId != saved.Id ||
            receipt.ReconciliationId != saved.ReconciliationId ||
            receipt.PreparationId != preparationId ||
            receipt.PlanId != planId ||
            receipt.CheckpointId != checkpointId ||
            receipt.SourceSnapshotId != saved.SourceSnapshotId ||
            receipt.CheckpointTargetObservationId != targetObservationId ||
            receipt.SourceRepositoryId != sourceRepositoryId ||
            receipt.RecordedBy != saved.RecordedBy ||
            receipt.TargetReceiptSha256 != saved.TargetReceiptSha256 ||
            receipt.SourceFullRefProofSha256 != saved.FullRefProofSha256 ||
            receipt.EvidenceSha256 != saved.EvidenceSha256 ||
            saved.RecordedAtUtc == default ||
            saved.RecordedAtUtc.Offset != TimeSpan.Zero ||
            saved.State != "Blocked" || saved.Phase != "WriteBlocked" ||
            saved.Attempts != 0 || saved.ReadyForExecution ||
            saved.CanPush || saved.CanRetry || saved.CanDispatch ||
            saved.MigrationComplete ||
            recon is null || recon.Id != receipt.ReconciliationId ||
            recon.PreparationId != preparationId || recon.PlanId != planId ||
            recon.CheckpointId != checkpointId ||
            recon.SourceSnapshotId != receipt.SourceSnapshotId ||
            recon.SourceRepositoryId != sourceRepositoryId ||
            recon.TargetObservationId != targetObservationId ||
            recon.ManifestSha256 != receipt.OperationManifestSha256 ||
            recon.SourceEvidenceSha256 != receipt.SourceEvidenceSha256 ||
            recon.FullRefProofSha256 != receipt.SourceFullRefProofSha256 ||
            recon.TargetReceiptSha256 != receipt.TargetReceiptSha256 ||
            recon.ReconciliationSha256 != receipt.ReconciliationSha256 ||
            recon.RecordedBy != receipt.RecordedBy ||
            recon.RecordedAtUtc != saved.RecordedAtUtc ||
            recon.ReadyForExecution ||
            recon.SourceRefCount != compared.SourceRefCount ||
            recon.TargetRefCount != compared.TargetRefCount ||
            recon.MatchingCount != compared.MatchingCount ||
            recon.CreateCandidateCount != compared.CreateCandidateCount ||
            recon.DivergedCount != compared.DivergedCount ||
            recon.TargetOnlyCount != compared.TargetOnlyCount ||
            sourceRow is null || sourceRow.RecordedBy != receipt.RecordedBy ||
            sourceRow.EvidenceSha256 != source.EvidenceSha256 ||
            sourceRow.SourceManifestRootSha256 != source.SourceManifestRootSha256 ||
            evs.Length != 1 || evs[0].Type != "Recorded" ||
            evs[0].ActorId != receipt.RecordedBy ||
            evs[0].EvidenceSha256 != receipt.EvidenceSha256 ||
            evs[0].OccurredAtUtc != saved.RecordedAtUtc ||
            human is null ||
            human.EvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            human.CandidateEvidenceSha256 != target.CandidateEvidenceSha256 ||
            human.ReviewedBy != target.ReviewedBy ||
            human.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.CanObserveTarget || human.CanPush || human.CanRetry ||
            human.CanDispatch || human.MigrationComplete ||
            humanEvents.Length != 1 ||
            humanEvents[0].Type != human.Decision ||
            humanEvents[0].ReviewerId != target.ReviewedBy ||
            humanEvents[0].EvidenceSha256 != human.EvidenceSha256 ||
            humanEvents[0].OccurredAtUtc != human.ReviewedAtUtc)
            throw Refused("LaterCheckpointHistoryIntegrityInvalid");

        try
        {
            var items = JsonSerializer.Deserialize<GitMirrorRefComparison[]>(
                recon.ItemsJson);
            if (items is null || !items.SequenceEqual(compared.Items))
                throw Refused("LaterCheckpointHistoryIntegrityInvalid");
        }
        catch (JsonException)
        { throw Refused("LaterCheckpointHistoryIntegrityInvalid"); }

        // Historical proof is inspectable after F-027 revocation or TTL
        // expiry, but no response asserts current reviewer eligibility.
        // Never serialize the sealed receipt, raw refs, OIDs or actor UUID.
        return new(2, preparationId, planId, checkpointId,
            receipt.SourceSnapshotId, targetObservationId,
            receipt.ReconciliationId, receipt.WorkpackId,
            receipt.EvidenceSha256, receipt.ReconciliationSha256,
            receipt.SourceFullRefProofSha256, receipt.TargetReceiptSha256,
            saved.RecordedAtUtc, compared.SourceRefCount,
            compared.TargetRefCount, compared.MatchingCount,
            compared.CreateCandidateCount, compared.DivergedCount,
            compared.TargetOnlyCount, "Blocked", "WriteBlocked",
            true, false, false, false, false, false, false);
    }
}
