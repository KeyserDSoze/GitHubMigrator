using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-029 remains an immutable operational *specification*. It has no GitHub
// adapter, no outbound HTTP dependency, no writable executor and no permit.
public sealed class PostgresIncrementalOperationPlanRepository(
    AppDbContext db, TimeProvider clock) : IIncrementalOperationPlanRepository
{
    public async Task<IncrementalOperationPlan> CreateAsync(
        Guid preparationId, Guid actorId, string expectedPreflightSha256,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            expectedPreflightSha256.Length != 64)
            throw new IncrementalOperationPlanException("InvalidOperationPlanRequest");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct)
            ?? throw new KeyNotFoundException("PreparationNotFound");
        await LockSourceAsync(run.SourceConnectionId, ct);

        var review = await new PostgresIncrementalReviewRepository(db, clock)
            .GetAsync(run.Id, ct);
        var target = await new PostgresIncrementalTargetPreflightRepository(db, clock)
            .GetAsync(run.Id, ct);
        if (run.State != IncrementalPreparationStates.Prepared ||
            review?.State != IncrementalReviewStates.ApprovedForPlanning ||
            !review.IsCurrent || target is null || !target.IsCurrent ||
            review.DryRunSha256 != target.ReviewSha256 ||
            review.EvidenceSha256 != run.EvidenceSha256)
            throw new IncrementalOperationPlanException("OperationPlanProofNotCurrent");
        if (target.EvidenceSha256 != expectedPreflightSha256)
            throw new IncrementalOperationPlanException("OperationPreflightDigestMismatch");
        if (target.Repositories.Count is < 1 or > 10 ||
            target.Repositories.Count != target.CheckedRepositories)
            throw new IncrementalOperationPlanException("OperationMappingInvalid");

        // F-027 integrity check must be re-evaluated here; passing a previous
        // human review does NOT immunize a preparation against later SQL edits.
        await VerifyPreparedJournalAsync(run, ct);

        var refs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var repo in target.Repositories)
        {
            // Canonical F-013 ref keys are JSON arrays [repositoryId,ref].
            // Aggregate server-side: O(10) scalar queries, not O(all refs) RAM.
            var count = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value"
                FROM incremental_preparation_items
                WHERE preparation_id = {run.Id} AND asset_type = 'ref'
                  AND source_id::jsonb ->> 0 = {repo.SourceRepositoryId}
                """).SingleAsync(ct);
            refs.Add(repo.SourceRepositoryId, count);
        }
        var steps = IncrementalOperationPlanIntegrity.BuildSteps(target.Repositories, refs);
        var manifestSha = IncrementalOperationPlanIntegrity.Hash(
            run.Id, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, target.Organization,
            review.DryRunSha256, run.EvidenceSha256!,
            target.EvidenceSha256, run.SensitiveItemCount, steps);

        // Source lock + unique (preparation,preflight digest) guarantees that
        // concurrent API replicas converge without mutating earlier versions.
        var existing = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == run.Id &&
                x.PreflightSha256 == target.EvidenceSha256, ct);
        if (existing is not null)
        {
            if (existing.ManifestSha256 != manifestSha)
                throw new IncrementalOperationPlanException("OperationPlanEvidenceMismatch");
            await tx.CommitAsync(ct);
            return (await GetAsync(run.Id, existing.Id, ct))!;
        }

        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        var created = new IncrementalOperationPlanRecord {
            Id = Guid.NewGuid(), PreparationId = run.Id,
            SnapshotId = run.SnapshotId,
            SourceConnectionId = run.SourceConnectionId,
            DestinationConnectionId = run.DestinationConnectionId,
            Organization = target.Organization, ReviewSha256 = review.DryRunSha256,
            PreparationSha256 = run.EvidenceSha256!,
            PreflightSha256 = target.EvidenceSha256, ManifestSha256 = manifestSha,
            CreatedAtUtc = now, CreatedBy = actorId,
            RepositoryCount = target.Repositories.Count, StepCount = steps.Count,
            ChangedRefCount = refs.Values.Sum(),
            SensitiveChangeCount = run.SensitiveItemCount
        };
        db.IncrementalOperationPlans.Add(created);
        db.IncrementalOperationSteps.AddRange(steps.Select(step =>
            new IncrementalOperationStepRecord {
                PlanId = created.Id, Ordinal = step.Ordinal,
                DependsOnOrdinal = step.DependsOnOrdinal,
                SourceRepositoryId = step.SourceRepositoryId,
                TargetRepositoryName = step.TargetRepositoryName,
                Kind = step.Kind, State = step.State,
                BlockerCode = step.BlockerCode, ChangedRefs = step.ChangedRefs
            }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(run.Id, created.Id, ct))!;
    }

    public async Task<IncrementalOperationPlan?> GetAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty) return null;
        var row = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct);
        return row is null ? null : await BuildViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<IncrementalOperationPlan>> ListAsync(
        Guid preparationId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty) return [];
        var rows = await db.IncrementalOperationPlans.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var output = new List<IncrementalOperationPlan>(rows.Length);
        foreach (var row in rows)
            output.Add(await BuildViewAsync(row, ct));
        return output;
    }

    public async Task<IncrementalExecutionAuthorizationAttempt> RequestAuthorizationAsync(
        Guid preparationId, Guid planId, Guid actorId,
        string expectedManifestSha256, bool acknowledgedRisks, CancellationToken ct)
    {
        if (!acknowledgedRisks || actorId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty)
            throw new IncrementalOperationPlanException("InvalidExecutionAuthorizationRequest");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        await LockSourceAsync(row.SourceConnectionId, ct);
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleAsync(x => x.Id == preparationId, ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        if (row.ManifestSha256 != expectedManifestSha256)
            throw new IncrementalOperationPlanException("OperationManifestDigestMismatch");
        if (actorId == row.CreatedBy || actorId == run.EnqueuedBy ||
            actorId == review?.ReviewerId)
            throw new IncrementalOperationPlanException("ExecutionReviewerMustBeIndependent");

        var view = await BuildViewAsync(row, ct);
        // Intentionally DENY all F-029 authorization attempts. F-028 never
        // proves repository absence/creation rights; there is no executor.
        // Future phases MUST NOT reinterpret this "Denied" audit as a grant.
        var code = !view.IsCurrent
            ? "OperationPlanNotCurrent"
            : "DestinationWritePermissionUnverified";
        var record = new IncrementalExecutionAuthorizationAttemptRecord {
            PlanId = row.Id, ActorId = actorId, AtUtc = clock.GetUtcNow(),
            ManifestSha256 = expectedManifestSha256,
            Decision = "Denied", ReasonCode = code
        };
        db.IncrementalExecutionAuthorizationAttempts.Add(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(record.Id, record.PlanId, record.ActorId, record.AtUtc,
            record.ManifestSha256, record.Decision, record.ReasonCode);
    }

    public async Task<IReadOnlyList<IncrementalExecutionAuthorizationAttempt>> EventsAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty) return [];
        var belongs = await db.IncrementalOperationPlans.AsNoTracking()
            .AnyAsync(x => x.Id == planId && x.PreparationId == preparationId, ct);
        if (!belongs) return [];
        var rows = await db.IncrementalExecutionAuthorizationAttempts.AsNoTracking()
            .Where(x => x.PlanId == planId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id).Select(x =>
            new IncrementalExecutionAuthorizationAttempt(x.Id, x.PlanId,
                x.ActorId, x.AtUtc, x.ManifestSha256,
                x.Decision, x.ReasonCode)).ToArray();
    }

    private async Task<IncrementalOperationPlan> BuildViewAsync(
        IncrementalOperationPlanRecord row, CancellationToken ct)
    {
        var raw = await db.IncrementalOperationSteps.AsNoTracking()
            .Where(x => x.PlanId == row.Id)
            .OrderBy(x => x.Ordinal).ToArrayAsync(ct);
        var steps = raw.Select(x => new IncrementalOperationStep(
            x.Ordinal, x.DependsOnOrdinal, x.SourceRepositoryId,
            x.TargetRepositoryName, x.Kind, x.State, x.BlockerCode,
            x.ChangedRefs)).ToArray();
        if (steps.Length != row.StepCount ||
            steps.Length != row.RepositoryCount * 6 ||
            steps.Length is < 6 or > 60 ||
            steps.Select(x => x.Ordinal).Where((v, i) => v != i + 1).Any() ||
            steps.Any(x => x.State != "Blocked" ||
                x.ChangedRefs < 0 || x.TargetRepositoryName.Length is < 1 or > 100))
            throw new IncrementalOperationPlanException("OperationPlanIntegrityFailed");

        var targets = steps.Where(x => x.Kind == "ReviewTargetName")
            .Select(x => new IncrementalTargetProbe(
                x.SourceRepositoryId, x.TargetRepositoryName, "GitMirror",
                "NotVisibleOrMissing")).ToArray();
        var counts = targets.ToDictionary(x => x.SourceRepositoryId,
            x => steps.First(s => s.SourceRepositoryId == x.SourceRepositoryId)
                .ChangedRefs, StringComparer.Ordinal);
        var canonical = IncrementalOperationPlanIntegrity.BuildSteps(targets, counts);
        if (!canonical.SequenceEqual(steps) ||
            row.ChangedRefCount != counts.Values.Sum() ||
            IncrementalOperationPlanIntegrity.Hash(
                row.PreparationId, row.SnapshotId,
                row.SourceConnectionId, row.DestinationConnectionId,
                row.Organization, row.ReviewSha256, row.PreparationSha256,
                row.PreflightSha256, row.SensitiveChangeCount, steps) !=
                    row.ManifestSha256)
            throw new IncrementalOperationPlanException("OperationPlanIntegrityFailed");

        var prepared = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.PreparationId, ct);
        var current = false;
        IncrementalTargetPreflightView? target = null;
        if (prepared is not null && prepared.SnapshotId == row.SnapshotId &&
            prepared.SourceConnectionId == row.SourceConnectionId &&
            prepared.DestinationConnectionId == row.DestinationConnectionId &&
            prepared.EvidenceSha256 == row.PreparationSha256 &&
            prepared.State == IncrementalPreparationStates.Prepared)
        {
            target = await new PostgresIncrementalTargetPreflightRepository(db, clock)
                .GetAsync(row.PreparationId, ct);
            current = target is not null && target.IsCurrent &&
                target.EvidenceSha256 == row.PreflightSha256 &&
                target.ReviewSha256 == row.ReviewSha256;
        }
        var blockers = target is null
            ? new[] { "OperationPlanStale", "ExecutionAdapterNotImplemented" }
            : IncrementalOperationPlanIntegrity.Blockers(target, row.SensitiveChangeCount)
                .Concat(current ? Array.Empty<string>() : new[] { "OperationPlanStale" })
                .Distinct().ToArray();

        return new(row.Id, row.PreparationId, row.SnapshotId,
            row.SourceConnectionId, row.DestinationConnectionId,
            row.Organization, row.ReviewSha256, row.PreparationSha256,
            row.PreflightSha256, row.ManifestSha256, row.CreatedAtUtc, row.CreatedBy,
            row.RepositoryCount, row.StepCount, row.ChangedRefCount,
            row.SensitiveChangeCount, current, false, blockers, steps);
    }

    private async Task VerifyPreparedJournalAsync(
        IncrementalPreparationRecord run, CancellationToken ct)
    {
        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == run.SnapshotId, ct);
        if (sync is null || sync.SourceConnectionId != run.SourceConnectionId ||
            sync.JournalVersion != 1)
            throw new IncrementalOperationPlanException("OperationJournalUnavailable");
        try
        {
            var summary = await new PostgresStagingRepository(db, clock)
                .GetChangeSummaryAsync(run.SnapshotId, ct);
            if (summary is null || summary.Total != run.ItemCount ||
                summary.SourceConnectionId != run.SourceConnectionId)
                throw new IncrementalOperationPlanException("OperationJournalMismatch");
        }
        catch (StagingChangeIntegrityException)
        {
            throw new IncrementalOperationPlanException("OperationJournalMismatch");
        }
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH journal AS (
                SELECT asset_type::text, source_id::text, change_kind::text,
                    previous_fingerprint::text, current_fingerprint::text,
                    revision,
                    CASE WHEN change_kind = 'Missing' THEN 'Removal'
                         WHEN asset_type IN ('acl','identity','group','membership')
                             THEN 'Security' ELSE 'SourceChange' END::text AS review_category,
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
            throw new IncrementalOperationPlanException("OperationJournalMismatch");

        // Guard against changes to either evidence or its stored digest.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0; var sensitive = 0;
        var rows = db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id)
            .OrderBy(x => x.AssetType).ThenBy(x => x.SourceId);
        await foreach (var item in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            var bytes = JsonSerializer.Serialize(new object?[] {
                item.AssetType, item.SourceId, item.ChangeKind,
                item.PreviousFingerprint, item.CurrentFingerprint,
                item.Revision, item.ReviewCategory, item.ReviewState
            }) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(bytes));
            count++;
            if (item.ReviewCategory != "SourceChange") sensitive++;
        }
        if (count != run.ItemCount || sensitive != run.SensitiveItemCount ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != run.EvidenceSha256)
            throw new IncrementalOperationPlanException("OperationJournalMismatch");
    }

    private Task LockSourceAsync(Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({sourceId.ToString()}), 732911)", ct);
}
