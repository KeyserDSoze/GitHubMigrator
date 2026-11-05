using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// A sealed, non-executable F-032 snapshot. No credential, Git pack,
// provider command or write authorization appears in this table.
public sealed class IncrementalGitRefReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid SnapshotId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public Guid TargetObservationId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string? SourceManifestRootSha256 { get; set; }
    public string TargetEvidenceSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public int SourceRefCount { get; set; }
    public int TargetRefCount { get; set; }
    public int MatchingCount { get; set; }
    public int CreateCandidateCount { get; set; }
    public int DivergedCount { get; set; }
    public int TargetOnlyCount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresIncrementalGitRefReconciliationStore(
    AppDbContext db, TimeProvider clock,
    PostgresVerifiedSourceGitRefReader sourceReader,
    PostgresGitHubTargetRefEvidenceStore targetEvidence)
    : IIncrementalGitRefReconciliationStore
{
    public async Task<IncrementalGitRefReconciliationView> CreateAsync(
        Guid preparationId, Guid planId, string sourceRepositoryId,
        Guid targetObservationId, string expectedManifestSha256,
        string expectedTargetEvidenceSha256, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var planRecord = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");

        // Same global source-scoped transaction/advisory lock as F-013 and
        // F-026/F-027/F-028/F-029/F-031. No target observation may replace the
        // current proof while this reconciliation is being committed.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planRecord.SourceConnectionId.ToString()}), 732911)",
            ct);
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.ManifestSha256 != expectedManifestSha256)
            throw new IncrementalGitRefReconciliationException("ReconciliationPlanNotCurrent");
        var mapping = plan.Steps.SingleOrDefault(x =>
            x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == sourceRepositoryId);
        if (mapping is null)
            throw new IncrementalGitRefReconciliationException("ReconciliationMappingInvalid");

        var target = await targetEvidence.ReadVerifiedSnapshotAsync(
            preparationId, planId, targetObservationId, ct);
        if (target is null)
            throw new KeyNotFoundException("TargetRefEvidenceNotFound");
        if (!target.Value.View.IsCurrent ||
            target.Value.View.EvidenceSha256 != expectedTargetEvidenceSha256 ||
            target.Value.View.SourceRepositoryId != sourceRepositoryId ||
            target.Value.View.TargetRepositoryName != mapping.TargetRepositoryName ||
            target.Value.View.ManifestSha256 != plan.ManifestSha256)
            throw new IncrementalGitRefReconciliationException("ReconciliationTargetNotCurrent");

        // F-014 historical sealed chunks (or explicitly complete legacy
        // inline snapshot) are read from the F-029 snapshot, never from
        // mutable staging_assets or an untrusted HTTP request body.
        var source = await sourceReader.ReadAsync(plan, sourceRepositoryId, ct);
        GitMirrorRefReconciliation result;
        try
        {
            result = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                target.Value.View.EvidenceSha256, source.Refs,
                target.Value.Observation.AsMirrorRefs()));
        }
        catch (GitMirrorRefReconciliationException ex)
        {
            throw new IncrementalGitRefReconciliationException(ex.Code);
        }

        var existing = await db.IncrementalGitRefReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == planId &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceEvidenceSha256 == source.SourceEvidenceSha256, ct);
        if (existing is not null)
        {
            if (existing.ReconciliationSha256 != result.ReconciliationSha256)
                throw new IncrementalGitRefReconciliationException("ReconciliationProofMismatch");
            await tx.CommitAsync(ct);
            return await ViewAsync(existing, ct);
        }

        var entry = new IncrementalGitRefReconciliationRecord {
            Id = Guid.NewGuid(), PlanId = plan.Id,
            SnapshotId = source.SnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = mapping.TargetRepositoryName,
            TargetObservationId = targetObservationId,
            ManifestSha256 = plan.ManifestSha256,
            SourceEvidenceSha256 = source.SourceEvidenceSha256,
            SourceManifestRootSha256 = source.RefManifestRootSha256,
            TargetEvidenceSha256 = target.Value.View.EvidenceSha256,
            ReconciliationSha256 = result.ReconciliationSha256,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                clock.GetUtcNow().ToUnixTimeMilliseconds()),
            CreatedBy = actorId,
            SourceRefCount = result.SourceRefCount,
            TargetRefCount = result.TargetRefCount,
            MatchingCount = result.MatchingCount,
            CreateCandidateCount = result.CreateCandidateCount,
            DivergedCount = result.DivergedCount,
            TargetOnlyCount = result.TargetOnlyCount,
            ItemsJson = JsonSerializer.Serialize(result.Items),
            ReadyForExecution = false
        };
        db.IncrementalGitRefReconciliations.Add(entry);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ViewAsync(entry, ct);
    }

    public async Task<IReadOnlyList<IncrementalGitRefReconciliationView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct);
        if (plan is null) return [];
        var rows = await db.IncrementalGitRefReconciliations.AsNoTracking()
            .Where(x => x.PlanId == planId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToArrayAsync(ct);
        var result = new List<IncrementalGitRefReconciliationView>(rows.Length);
        foreach (var row in rows)
            result.Add(await ViewAsync(row, ct, plan));
        return result;
    }

    public async Task<IncrementalGitRefReconciliationPage?> PageAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string? after, int take, CancellationToken ct)
    {
        if (take is < 1 or > 100 || (after?.Length ?? 0) > 255)
            throw new IncrementalGitRefReconciliationException("InvalidReconciliationPage");
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct);
        if (plan is null) return null;
        var row = await db.IncrementalGitRefReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reconciliationId &&
                x.PlanId == planId, ct);
        if (row is null) return null;
        var summary = await ViewAsync(row, ct, plan);
        var items = ValidateAndRecompute(row).Items
            .Where(x => after is null ||
                string.Compare(x.Name, after, StringComparison.Ordinal) > 0)
            .Take(take + 1).ToArray();
        var selected = items.Take(take).ToArray();
        return new(summary, selected, items.Length > take ? selected[^1].Name : null);
    }

    private async Task<IncrementalGitRefReconciliationView> ViewAsync(
        IncrementalGitRefReconciliationRecord row, CancellationToken ct,
        IncrementalOperationPlan? knownPlan = null)
    {
        _ = ValidateAndRecompute(row);
        var plan = knownPlan ?? await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync((await db.IncrementalOperationPlans.AsNoTracking()
                .SingleAsync(x => x.Id == row.PlanId, ct)).PreparationId,
                row.PlanId, ct);
        if (plan is null || plan.ManifestSha256 != row.ManifestSha256 ||
            plan.SnapshotId != row.SnapshotId ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == row.SourceRepositoryId &&
                x.TargetRepositoryName == row.TargetRepositoryName))
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");

        // Validate which source evidence mode was sealed. An attacker
        // cannot turn a durable F-014 observation into legacy mode simply
        // by nulling source_manifest_root_sha256 in one SQL row.
        var inventory = await new PostgresDiscoveryRepository(db)
            .GetWithoutRefsAsync(row.SnapshotId, ct);
        if (inventory is null ||
            inventory.ConnectionId != plan.SourceConnectionId ||
            inventory.RefsExternalized != (row.SourceManifestRootSha256 is not null))
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");

        // Never infer currency from persisted counters: F-031 may have a
        // newer, independently validated target observation, or the planning
        // review/credential may have expired.
        var target = await targetEvidence.ReadVerifiedSnapshotAsync(
            plan.PreparationId, row.PlanId, row.TargetObservationId, ct);
        if (target is null ||
            target.Value.View.EvidenceSha256 != row.TargetEvidenceSha256 ||
            target.Value.View.SourceRepositoryId != row.SourceRepositoryId)
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");

        var lastId = await db.IncrementalGitRefReconciliations.AsNoTracking()
            .Where(x => x.PlanId == row.PlanId &&
                x.SourceRepositoryId == row.SourceRepositoryId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => x.Id).FirstAsync(ct);
        var sourceRootCurrent = row.SourceManifestRootSha256 is null ||
            await db.DiscoveryRefManifests.AsNoTracking().AnyAsync(x =>
                x.JobId == row.SnapshotId &&
                x.RootSha256 == row.SourceManifestRootSha256, ct);
        var current = plan.IsCurrent && target.Value.View.IsCurrent &&
            lastId == row.Id && sourceRootCurrent;
        return new(row.Id, row.PlanId, row.SourceRepositoryId,
            row.TargetRepositoryName, row.TargetObservationId, row.SnapshotId,
            row.ManifestSha256, row.SourceEvidenceSha256,
            row.SourceManifestRootSha256, row.TargetEvidenceSha256,
            row.ReconciliationSha256, row.CreatedAtUtc, row.SourceRefCount,
            row.TargetRefCount, row.MatchingCount, row.CreateCandidateCount,
            row.DivergedCount, row.TargetOnlyCount, current, false);
    }

    private static GitMirrorRefReconciliation ValidateAndRecompute(
        IncrementalGitRefReconciliationRecord row)
    {
        if (row.ReadyForExecution)
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");
        GitMirrorRefComparison[] items;
        try { items = JsonSerializer.Deserialize<GitMirrorRefComparison[]>(row.ItemsJson)
                ?? throw new JsonException(); }
        catch (JsonException)
        { throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed"); }
        if (items.Length > 2 * GitMirrorRefReconciler.MaxRefsPerSide ||
            items.Any(x => x is null))
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");

        // Reconstruct both complete, sorted evidence sets from the persisted
        // per-ref comparisons. A changed name/OID/disposition/count/proof
        // cannot pass the independent F-030 recomputation.
        try
        {
            var source = items.Where(x => x.SourceObjectId is not null)
                .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
            var target = items.Where(x => x.TargetObjectId is not null)
                .Select(x => new GitMirrorRef(x.Name, x.TargetObjectId!)).ToArray();
            var recomputed = GitMirrorRefReconciler.Compare(new(
                row.PlanId, row.ManifestSha256, row.SourceEvidenceSha256,
                row.TargetEvidenceSha256, source, target));
            if (row.SourceRefCount != recomputed.SourceRefCount ||
                row.TargetRefCount != recomputed.TargetRefCount ||
                row.MatchingCount != recomputed.MatchingCount ||
                row.CreateCandidateCount != recomputed.CreateCandidateCount ||
                row.DivergedCount != recomputed.DivergedCount ||
                row.TargetOnlyCount != recomputed.TargetOnlyCount ||
                row.ReconciliationSha256 != recomputed.ReconciliationSha256 ||
                !items.SequenceEqual(recomputed.Items))
                throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");
            return recomputed;
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new IncrementalGitRefReconciliationException("ReconciliationIntegrityFailed");
        }
    }
}
