using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Database-only reviewed planning evidence. NO destination HTTP/credential
// dependency, effective permission inference or executable migration command.
public sealed class PostgresIncrementalReviewRepository(AppDbContext db, TimeProvider clock)
    : IIncrementalReviewRepository
{
    public async Task<IncrementalReviewView?> GetAsync(
        Guid preparationId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty) return null;
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        if (review is null) return null;
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleAsync(x => x.Id == preparationId, ct);
        return await ViewAsync(review, run, ct);
    }

    public async Task<IReadOnlyList<IncrementalReviewEventView>> EventsAsync(
        Guid preparationId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty) return [];
        var rows = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id)
            .Select(x => new IncrementalReviewEventView(
                x.Id, x.PreparationId, x.EventType, x.ActorId,
                x.OccurredAtUtc, x.DryRunSha256, x.ReasonCode)).ToArray();
    }

    public async Task<IncrementalReviewView> DryRunAsync(
        Guid preparationId, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty)
            throw new IncrementalReviewException("InvalidReviewContext");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct)
            ?? throw new KeyNotFoundException("PreparationNotFound");
        await LockSourceAsync(run.SourceConnectionId, ct);
        // Fence against in-flight worker completion and concurrent dry-run.
        var current = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET updated_at_utc = updated_at_utc
            WHERE id = {preparationId} AND state = 'Prepared'
            """, ct);
        if (current != 1)
            throw new IncrementalReviewException("PreparationNotReady");

        // The source lock prevents later staging commits from racing our
        // journal comparison. Both connection metadata checks are read-only.
        var (source, destination) = await VerifyContextAsync(run, ct);
        var (count, sensitive, removal) = await VerifyEvidenceAsync(run, ct);
        var hash = DryRunHash(run, removal, source.ValidatedAtUtc,
            destination.ValidatedAtUtc);
        var now = clock.GetUtcNow();
        var existing = await db.IncrementalReviews
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        if (existing is not null && existing.State != IncrementalReviewStates.DryRunReady)
            throw new IncrementalReviewException("ReviewAlreadyDecided");

        var changed = existing is null || existing.DryRunSha256 != hash;
        if (existing is null)
        {
            existing = new IncrementalReviewRecord { PreparationId = preparationId };
            db.IncrementalReviews.Add(existing);
        }
        if (changed)
        {
            existing.State = IncrementalReviewStates.DryRunReady;
            existing.EvidenceSha256 = run.EvidenceSha256!;
            existing.DryRunSha256 = hash;
            existing.DryRunAtUtc = now;
            existing.SourceValidatedAtUtc = source.ValidatedAtUtc;
            existing.DestinationValidatedAtUtc = destination.ValidatedAtUtc;
            existing.ItemCount = count;
            existing.SensitiveItemCount = sensitive;
            existing.RemovalItemCount = removal;
            existing.ReviewerId = null;
            existing.ReviewedAtUtc = null;
            existing.ReasonCode = null;
            existing.SensitiveAcknowledged = false;
            db.IncrementalReviewEvents.Add(new IncrementalReviewEventRecord {
                PreparationId = preparationId,
                EventType = "DryRunReady", ActorId = actorId,
                OccurredAtUtc = now, DryRunSha256 = hash
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ViewAsync(existing, run, ct);
    }

    public async Task<IncrementalReviewView> DecideAsync(
        Guid preparationId, Guid actorId, string decision,
        string expectedDryRunSha256, bool acknowledgeSensitive,
        string? reasonCode, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            decision is not ("ApproveForPlanning" or "Reject"))
            throw new IncrementalReviewException("InvalidReviewDecision");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct)
            ?? throw new KeyNotFoundException("PreparationNotFound");
        await LockSourceAsync(run.SourceConnectionId, ct);
        var existing = await db.IncrementalReviews
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct)
            ?? throw new IncrementalReviewException("DryRunRequired");
        var state = decision == "ApproveForPlanning"
            ? IncrementalReviewStates.ApprovedForPlanning
            : IncrementalReviewStates.Rejected;

        // Do not allow an idempotent retry to create a second audit event.
        // A different reviewer, reason, hash or decision is a hard conflict.
        if (existing.State != IncrementalReviewStates.DryRunReady)
        {
            if (existing.State == state && existing.ReviewerId == actorId &&
                existing.DryRunSha256 == expectedDryRunSha256 &&
                existing.ReasonCode == reasonCode &&
                existing.SensitiveAcknowledged ==
                    (decision == "ApproveForPlanning" && acknowledgeSensitive))
            {
                await transaction.CommitAsync(ct);
                return await ViewAsync(existing, run, ct);
            }
            throw new IncrementalReviewException("ReviewDecisionConflict");
        }

        // Row lock serializes concurrent approvals/rejections across API pods.
        var locked = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparation_reviews SET state = state
            WHERE preparation_id = {preparationId} AND state = 'DryRunReady'
            """, ct);
        if (locked != 1)
            throw new IncrementalReviewException("ReviewDecisionConflict");
        if (run.EnqueuedBy == actorId)
            throw new IncrementalReviewException("ReviewerMustBeIndependent");
        if (run.State != IncrementalPreparationStates.Prepared)
            throw new IncrementalReviewException("PreparationNotReady");
        if (existing.DryRunSha256 != expectedDryRunSha256)
            throw new IncrementalReviewException("ReviewDigestMismatch");
        if (decision == "ApproveForPlanning" && existing.SensitiveItemCount > 0 &&
            !acknowledgeSensitive)
            throw new IncrementalReviewException("SensitiveReviewAcknowledgmentRequired");
        var (source, destination) = await VerifyContextAsync(run, ct);
        if (source.ValidatedAtUtc != existing.SourceValidatedAtUtc ||
            destination.ValidatedAtUtc != existing.DestinationValidatedAtUtc)
            throw new IncrementalReviewException("DryRunOutdated");
        var (count, sensitive, removal) = await VerifyEvidenceAsync(run, ct);
        if (count != existing.ItemCount || sensitive != existing.SensitiveItemCount ||
            removal != existing.RemovalItemCount ||
            DryRunHash(run, removal, source.ValidatedAtUtc,
                destination.ValidatedAtUtc) != existing.DryRunSha256)
            throw new IncrementalReviewException("DryRunOutdated");

        var now = clock.GetUtcNow();
        existing.State = state;
        existing.ReviewerId = actorId;
        existing.ReviewedAtUtc = now;
        existing.ReasonCode = reasonCode;
        existing.SensitiveAcknowledged =
            decision == "ApproveForPlanning" && acknowledgeSensitive;
        db.IncrementalReviewEvents.Add(new IncrementalReviewEventRecord {
            PreparationId = preparationId,
            EventType = state, ActorId = actorId,
            OccurredAtUtc = now, DryRunSha256 = existing.DryRunSha256,
            ReasonCode = reasonCode
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ViewAsync(existing, run, ct);
    }

    public async Task<IncrementalReviewView> RevokeAsync(
        Guid preparationId, Guid actorId, string expectedDryRunSha256,
        string reasonCode, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty)
            throw new IncrementalReviewException("InvalidReviewDecision");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct)
            ?? throw new KeyNotFoundException("PreparationNotFound");
        await LockSourceAsync(run.SourceConnectionId, ct);
        var existing = await db.IncrementalReviews
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct)
            ?? throw new IncrementalReviewException("DryRunRequired");
        if (run.EnqueuedBy == actorId)
            throw new IncrementalReviewException("ReviewerMustBeIndependent");
        if (existing.State == IncrementalReviewStates.Revoked &&
            existing.ReviewerId == actorId &&
            existing.DryRunSha256 == expectedDryRunSha256 &&
            existing.ReasonCode == reasonCode)
        {
            await transaction.CommitAsync(ct);
            return await ViewAsync(existing, run, ct);
        }
        if (existing.State != IncrementalReviewStates.ApprovedForPlanning)
            throw new IncrementalReviewException("ReviewDecisionConflict");
        if (existing.DryRunSha256 != expectedDryRunSha256)
            throw new IncrementalReviewException("ReviewDigestMismatch");
        var locked = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparation_reviews SET state = state
            WHERE preparation_id = {preparationId} AND state = 'ApprovedForPlanning'
            """, ct);
        if (locked != 1)
            throw new IncrementalReviewException("ReviewDecisionConflict");

        var now = clock.GetUtcNow();
        existing.State = IncrementalReviewStates.Revoked;
        existing.ReviewerId = actorId;
        existing.ReviewedAtUtc = now;
        existing.ReasonCode = reasonCode;
        db.IncrementalReviewEvents.Add(new IncrementalReviewEventRecord {
            PreparationId = preparationId,
            EventType = IncrementalReviewStates.Revoked,
            ActorId = actorId, OccurredAtUtc = now,
            DryRunSha256 = existing.DryRunSha256, ReasonCode = reasonCode
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ViewAsync(existing, run, ct);
    }

    // The security gate must not use current staging rows alone. Those rows
    // may be overwritten by a later scan. It compares the pinned F-025 source
    // journal to the prepared F-026 rows, including fingerprints, kinds and
    // review classification, in BOTH multiset directions.
    private async Task<(int Count, int Sensitive, int Removal)> VerifyEvidenceAsync(
        IncrementalPreparationRecord run, CancellationToken ct)
    {
        if (run.State != IncrementalPreparationStates.Prepared ||
            run.EvidenceSha256 is not { Length: 64 })
            throw new IncrementalReviewException("PreparationNotReady");

        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == run.SnapshotId, ct);
        if (sync is null || sync.SourceConnectionId != run.SourceConnectionId ||
            sync.JournalVersion != 1)
            throw new IncrementalReviewException("IncrementalJournalUnavailable");
        try
        {
            var summary = await new PostgresStagingRepository(db, clock)
                .GetChangeSummaryAsync(run.SnapshotId, ct);
            if (summary is null || summary.SourceConnectionId != run.SourceConnectionId ||
                summary.Total != run.ItemCount)
                throw new IncrementalReviewException("ReviewEvidenceMismatch");
        }
        catch (StagingChangeIntegrityException)
        {
            throw new IncrementalReviewException("ReviewEvidenceMismatch");
        }

        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH journal AS (
                SELECT asset_type, source_id, change_kind, previous_fingerprint,
                    current_fingerprint, revision,
                    CASE WHEN change_kind = 'Missing' THEN 'Removal'
                        WHEN asset_type IN ('acl','identity','group','membership')
                            THEN 'Security' ELSE 'SourceChange' END AS review_category,
                    'NeedsReview'::text AS review_state
                FROM staging_asset_changes
                WHERE snapshot_id = {run.SnapshotId}
                  AND source_connection_id = {run.SourceConnectionId}
            ),
            prepared AS (
                SELECT asset_type::text, source_id::text, change_kind::text,
                    previous_fingerprint::text, current_fingerprint::text,
                    revision, review_category::text, review_state::text
                FROM incremental_preparation_items
                WHERE preparation_id = {run.Id}
            ),
            delta AS (
                (SELECT * FROM journal EXCEPT ALL SELECT * FROM prepared)
                UNION ALL
                (SELECT * FROM prepared EXCEPT ALL SELECT * FROM journal)
            )
            SELECT EXISTS (SELECT 1 FROM delta) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new IncrementalReviewException("ReviewEvidenceMismatch");

        var count = 0;
        var sensitive = 0;
        var removal = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rows = db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id)
            .OrderBy(x => x.AssetType).ThenBy(x => x.SourceId);
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            var preimage = JsonSerializer.Serialize(new object?[] {
                row.AssetType, row.SourceId, row.ChangeKind,
                row.PreviousFingerprint, row.CurrentFingerprint,
                row.Revision, row.ReviewCategory, row.ReviewState
            }) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(preimage));
            count++;
            if (row.ReviewCategory != "SourceChange") sensitive++;
            if (row.ChangeKind == "Missing") removal++;
        }
        if (count != run.ItemCount || sensitive != run.SensitiveItemCount ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != run.EvidenceSha256)
            throw new IncrementalReviewException("ReviewEvidenceMismatch");
        return (count, sensitive, removal);
    }

    private async Task<(ConnectionRecord Source, ConnectionRecord Destination)>
        VerifyContextAsync(IncrementalPreparationRecord run, CancellationToken ct)
    {
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == run.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        if (latest != run.SnapshotId)
            throw new IncrementalReviewException("IncrementalSnapshotSuperseded");
        var source = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.DestinationConnectionId, ct);
        if (source is null || destination is null ||
            source.Direction != ConnectionDirection.Source ||
            !ProviderKeys.IsAzureDevOpsSource(source.ProviderKey) ||
            source.ValidationCode != "Verified" ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified")
            throw new IncrementalReviewException("ReviewConnectionNotVerified");
        return (source, destination);
    }

    private static string DryRunHash(
        IncrementalPreparationRecord run, int removal,
        DateTimeOffset? sourceValidated, DateTimeOffset? destinationValidated)
    {
        var payload = JsonSerializer.Serialize(new object?[] {
            "incremental-dry-run-v1", run.Id.ToString("D"), run.SnapshotId.ToString("D"),
            run.SourceConnectionId.ToString("D"),
            run.DestinationConnectionId.ToString("D"), run.EvidenceSha256,
            run.ItemCount, run.SensitiveItemCount, removal,
            sourceValidated?.ToUniversalTime().ToString("O"),
            destinationValidated?.ToUniversalTime().ToString("O")
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private Task LockSourceAsync(Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({sourceId.ToString()}), 732911)", ct);

    private async Task<IncrementalReviewView> ViewAsync(
        IncrementalReviewRecord review,
        IncrementalPreparationRecord run, CancellationToken ct)
    {
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == run.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var source = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.DestinationConnectionId, ct);
        var current = latest == run.SnapshotId &&
            run.State == IncrementalPreparationStates.Prepared &&
            run.EvidenceSha256 == review.EvidenceSha256 &&
            source?.ValidationCode == "Verified" &&
            destination?.ValidationCode == "Verified" &&
            source.ValidatedAtUtc == review.SourceValidatedAtUtc &&
            destination.ValidatedAtUtc == review.DestinationValidatedAtUtc;
        return new(review.PreparationId, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, review.State, review.EvidenceSha256,
            review.DryRunSha256, review.ItemCount, review.SensitiveItemCount,
            review.RemovalItemCount, review.DryRunAtUtc, review.ReviewerId,
            review.ReviewedAtUtc, review.ReasonCode,
            review.SensitiveAcknowledged, current);
    }
}
