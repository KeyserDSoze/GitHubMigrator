using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Read-only observation ledger, not an execution queue. The SQL CHECK on
// ready_for_execution enforces the non-executable phase boundary.
public sealed class GitHubTargetRefEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? DestinationValidatedAtUtc { get; set; }
    public int RefCount { get; set; }
    public string RefsJson { get; set; } = "[]";
    public Guid CreatedBy { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class PostgresGitHubTargetRefEvidenceStore(
    AppDbContext db, TimeProvider clock) : IGitHubTargetRefEvidenceStore
{
    private const int MaxListResults = 20;

    public async Task<GitHubTargetRefEvidenceView> SaveAsync(
        IncrementalOperationPlan plan, string sourceRepositoryId,
        Guid actorId, DateTimeOffset? destinationValidatedAtUtc,
        GitHubRefObservation observation, CancellationToken ct)
    {
        if (plan.Id == Guid.Empty || actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            observation.Refs.Count > GitMirrorRefReconciler.MaxRefsPerSide)
            throw new GitHubTargetRefEvidenceException("InvalidRefEvidenceRequest");

        var sealedEvidence = GitHubRefObservationIntegrity.Seal(
            observation.Organization, observation.Repository,
            observation.ObservedAtUtc, observation.Refs);
        if (sealedEvidence.EvidenceSha256 != observation.EvidenceSha256)
            throw new GitHubTargetRefEvidenceException("RefEvidenceProviderMismatch");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.SourceConnectionId.ToString()}), 732911)",
            ct);

        var current = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(plan.PreparationId, plan.Id, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.DestinationConnectionId, ct);
        var match = current?.Steps.SingleOrDefault(x =>
            x.Kind == "ReviewTargetName" && x.SourceRepositoryId == sourceRepositoryId);
        if (current is null || !current.IsCurrent ||
            current.ManifestSha256 != plan.ManifestSha256 ||
            current.SnapshotId != plan.SnapshotId ||
            current.DestinationConnectionId != plan.DestinationConnectionId ||
            match is null ||
            !string.Equals(match.TargetRepositoryName, observation.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            destination is null ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != destinationValidatedAtUtc ||
            !string.Equals(destination.Organization, observation.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Organization, observation.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubTargetRefEvidenceException("RefEvidenceContextChanged");

        // Reject future and already-expired observations. A newer GET does
        // not make an older observation current even if both are retained.
        var age = clock.GetUtcNow() - observation.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(5))
            throw new GitHubTargetRefEvidenceException("RefEvidenceObservationStale");

        var previous = await db.GitHubTargetRefEvidence.AsNoTracking()
            .Where(x => x.PlanId == plan.Id &&
                x.SourceRepositoryId == sourceRepositoryId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (previous is not null && previous.ObservedAtUtc > observation.ObservedAtUtc)
            throw new GitHubTargetRefEvidenceException("RefEvidenceObservationSuperseded");

        var replay = await db.GitHubTargetRefEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == plan.Id &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.EvidenceSha256 == sealedEvidence.EvidenceSha256, ct);
        if (replay is not null)
        {
            await tx.CommitAsync(ct);
            return await ViewAsync(replay, ct);
        }

        var record = new GitHubTargetRefEvidenceRecord {
            Id = Guid.NewGuid(), PlanId = plan.Id,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = match.TargetRepositoryName,
            Organization = current.Organization,
            ManifestSha256 = current.ManifestSha256,
            EvidenceSha256 = sealedEvidence.EvidenceSha256,
            ObservedAtUtc = sealedEvidence.ObservedAtUtc,
            DestinationValidatedAtUtc = destinationValidatedAtUtc,
            RefCount = sealedEvidence.Refs.Count,
            RefsJson = JsonSerializer.Serialize(sealedEvidence.Refs),
            CreatedBy = actorId, ReadyForExecution = false
        };
        db.GitHubTargetRefEvidence.Add(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ViewAsync(record, ct);
    }

    public async Task<IReadOnlyList<GitHubTargetRefEvidenceView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct);
        if (plan is null) return [];
        var rows = await db.GitHubTargetRefEvidence.AsNoTracking()
            .Where(x => x.PlanId == planId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Take(MaxListResults).ToArrayAsync(ct);
        var result = new List<GitHubTargetRefEvidenceView>(rows.Length);
        foreach (var row in rows) result.Add(await ViewAsync(row, ct, plan));
        return result;
    }

    public async Task<GitHubTargetRefEvidencePage?> PageAsync(
        Guid preparationId, Guid planId, Guid observationId,
        string? after, int take, CancellationToken ct)
    {
        if (take is < 1 or > 100)
            throw new GitHubTargetRefEvidenceException("InvalidRefEvidencePage");
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct);
        if (plan is null) return null;
        var row = await db.GitHubTargetRefEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == observationId, ct);
        if (row is null) return null;
        var view = await ViewAsync(row, ct, plan);
        var items = ParseVerified(row).Refs.Where(x =>
            after is null || string.Compare(x.Name, after, StringComparison.Ordinal) > 0)
            .Take(take + 1).ToArray();
        var page = items.Take(take).ToArray();
        return new(view, page,
            items.Length > take ? page[^1].Name : null);
    }

    // Internal F-032 access: one complete, SHA-verified immutable target
    // observation, never an unvalidated request-body ref list.
    public async Task<(GitHubTargetRefEvidenceView View,
        GitHubRefObservation Observation)?> ReadVerifiedSnapshotAsync(
        Guid preparationId, Guid planId, Guid observationId,
        CancellationToken ct)
    {
        var plan = await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync(preparationId, planId, ct);
        if (plan is null) return null;
        var row = await db.GitHubTargetRefEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observationId && x.PlanId == planId, ct);
        if (row is null) return null;
        return (await ViewAsync(row, ct, plan), ParseVerified(row));
    }

    private async Task<GitHubTargetRefEvidenceView> ViewAsync(
        GitHubTargetRefEvidenceRecord row, CancellationToken ct,
        IncrementalOperationPlan? knownPlan = null)
    {
        var proof = ParseVerified(row);
        var plan = knownPlan ?? await new PostgresIncrementalOperationPlanRepository(db, clock)
            .GetAsync((await db.IncrementalOperationPlans.AsNoTracking()
                .SingleAsync(x => x.Id == row.PlanId, ct)).PreparationId, row.PlanId, ct);
        if (plan is null || plan.ManifestSha256 != row.ManifestSha256 ||
            !string.Equals(plan.Organization, row.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == row.SourceRepositoryId &&
                x.TargetRepositoryName == row.TargetRepositoryName))
            throw new GitHubTargetRefEvidenceException("RefEvidenceIntegrityFailed");

        var dest = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.DestinationConnectionId, ct);
        var age = clock.GetUtcNow() - row.ObservedAtUtc;
        var newest = await db.GitHubTargetRefEvidence.AsNoTracking()
            .Where(x => x.PlanId == row.PlanId &&
                x.SourceRepositoryId == row.SourceRepositoryId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => x.Id).FirstAsync(ct);
        var current = plan.IsCurrent && newest == row.Id &&
            age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5) &&
            dest is not null && dest.ValidationCode == "Verified" &&
            dest.ValidatedAtUtc == row.DestinationValidatedAtUtc;
        return new(row.Id, row.PlanId, row.SourceRepositoryId,
            row.TargetRepositoryName, row.Organization, row.ManifestSha256,
            proof.EvidenceSha256, row.ObservedAtUtc, row.RefCount, current, false);
    }

    private static GitHubRefObservation ParseVerified(
        GitHubTargetRefEvidenceRecord record)
    {
        if (record.ReadyForExecution)
            throw new GitHubTargetRefEvidenceException("RefEvidenceIntegrityFailed");
        GitHubObservedRef[] refs;
        try
        {
            refs = JsonSerializer.Deserialize<GitHubObservedRef[]>(record.RefsJson)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new GitHubTargetRefEvidenceException("RefEvidenceIntegrityFailed");
        }
        try
        {
            var sealedProof = GitHubRefObservationIntegrity.Seal(
                record.Organization, record.TargetRepositoryName,
                record.ObservedAtUtc, refs);
            if (record.RefCount != refs.Length ||
                record.EvidenceSha256 != sealedProof.EvidenceSha256)
                throw new GitHubTargetRefEvidenceException("RefEvidenceIntegrityFailed");
            return sealedProof;
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubTargetRefEvidenceException("RefEvidenceIntegrityFailed");
        }
    }
}
