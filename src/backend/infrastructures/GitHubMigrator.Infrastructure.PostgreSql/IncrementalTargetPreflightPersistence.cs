using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-028 persists sanitized live GET observations. It never writes to GitHub,
// never stores a token, and never claims target creation permissions.
public sealed class PostgresIncrementalTargetPreflightRepository(
    AppDbContext db, TimeProvider clock) : IIncrementalTargetPreflightRepository
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Exists", "NotVisibleOrMissing", "Unauthorized", "Forbidden",
        "RateLimited", "InvalidResponse", "Unavailable"
    };

    public async Task<IncrementalTargetPreflightView> SaveAsync(
        IncrementalTargetPreflightEvidence evidence, CancellationToken ct)
    {
        if (evidence.PreparationId == Guid.Empty ||
            evidence.SnapshotId == Guid.Empty || evidence.ActorId == Guid.Empty ||
            evidence.Repositories.Count is < 1 or > 10 ||
            evidence.Repositories.Any(x => !AllowedStatuses.Contains(x.Status) ||
                string.IsNullOrEmpty(x.SourceRepositoryId) ||
                string.IsNullOrEmpty(x.TargetRepositoryName) ||
                x.SourceRepositoryId.Length > 256 || x.TargetRepositoryName.Length > 100 ||
                x.HistoryStrategy.Length > 40) ||
            evidence.Repositories.Select(x => x.SourceRepositoryId)
                .Distinct(StringComparer.Ordinal).Count() != evidence.Repositories.Count ||
            evidence.Repositories.Select(x => x.TargetRepositoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != evidence.Repositories.Count)
            throw new IncrementalTargetPreflightException("InvalidTargetPreflightEvidence");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({evidence.SourceConnectionId.ToString()}), 732911)", ct);

        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == evidence.PreparationId, ct)
            ?? throw new IncrementalTargetPreflightException("PreparationNotFound");
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == evidence.PreparationId, ct)
            ?? throw new IncrementalTargetPreflightException("PlanningReviewRequired");
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == evidence.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var source = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == evidence.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == evidence.DestinationConnectionId, ct);

        // Revalidate AFTER the provider calls, inside the same source-locked
        // transaction. Old/revoked approvals, stale snapshots and revalidated
        // credentials can never publish observations as "current".
        if (run.State != IncrementalPreparationStates.Prepared ||
            run.SnapshotId != evidence.SnapshotId ||
            run.SourceConnectionId != evidence.SourceConnectionId ||
            run.DestinationConnectionId != evidence.DestinationConnectionId ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.DryRunSha256 != evidence.ReviewSha256 ||
            review.EvidenceSha256 != run.EvidenceSha256 ||
            latest != run.SnapshotId ||
            source is null || destination is null ||
            source.ValidationCode != "Verified" ||
            destination.ValidationCode != "Verified" ||
            source.Direction != ConnectionDirection.Source ||
            destination.Direction != ConnectionDirection.Destination ||
            source.ProviderKey != "azure-devops-services" ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            source.ValidatedAtUtc != evidence.SourceValidatedAtUtc ||
            destination.ValidatedAtUtc != evidence.DestinationValidatedAtUtc ||
            review.SourceValidatedAtUtc != evidence.SourceValidatedAtUtc ||
            review.DestinationValidatedAtUtc != evidence.DestinationValidatedAtUtc ||
            !string.Equals(destination.Organization, evidence.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new IncrementalTargetPreflightException("TargetPreflightContextChanged");

        var existing = await db.IncrementalTargetPreflights
            .SingleOrDefaultAsync(x => x.PreparationId == evidence.PreparationId, ct);
        if (existing is not null &&
            existing.CheckedAtUtc > evidence.CheckedAtUtc)
            throw new IncrementalTargetPreflightException("TargetObservationSuperseded");
        if (existing is not null &&
            existing.EvidenceSha256 == evidence.EvidenceSha256)
        {
            await tx.CommitAsync(ct);
            return await BuildViewAsync(existing, run, ct);
        }

        var collisions = evidence.Repositories.Count(x => x.Status == "Exists");
        var uncertain = evidence.Repositories.Count - collisions;
        var current = existing ?? new IncrementalTargetPreflightRecord {
            PreparationId = evidence.PreparationId
        };
        if (existing is null) db.IncrementalTargetPreflights.Add(current);
        current.Organization = evidence.Organization;
        current.ReviewSha256 = evidence.ReviewSha256;
        current.EvidenceSha256 = evidence.EvidenceSha256;
        current.CheckedAtUtc = evidence.CheckedAtUtc;
        current.Revision = (existing?.Revision ?? 0) + 1;
        current.State = evidence.State;
        current.MembershipStatus = evidence.MembershipStatus;
        current.MembershipRole = evidence.MembershipRole;
        current.PrivateRepositoryPolicy = evidence.PrivateRepositoryPolicy;
        current.SourceValidatedAtUtc = evidence.SourceValidatedAtUtc;
        current.DestinationValidatedAtUtc = evidence.DestinationValidatedAtUtc;
        current.RepositoryCount = evidence.Repositories.Count;
        current.VisibleCollisions = collisions;
        current.UncertainRepositories = uncertain;

        if (existing is not null)
        {
            await db.IncrementalTargetProbes.Where(x =>
                x.PreparationId == evidence.PreparationId).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        db.IncrementalTargetProbes.AddRange(evidence.Repositories.Select(x =>
            new IncrementalTargetProbeRecord {
                PreparationId = evidence.PreparationId,
                SourceRepositoryId = x.SourceRepositoryId,
                TargetRepositoryName = x.TargetRepositoryName,
                HistoryStrategy = x.HistoryStrategy,
                Status = x.Status
            }));
        db.IncrementalTargetPreflightEvents.Add(new IncrementalTargetPreflightEventRecord {
            PreparationId = evidence.PreparationId, ActorId = evidence.ActorId,
            CheckedAtUtc = evidence.CheckedAtUtc,
            EvidenceSha256 = evidence.EvidenceSha256,
            State = evidence.State, Revision = current.Revision
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await BuildViewAsync(current, run, ct);
    }

    public async Task<IncrementalTargetPreflightView?> GetAsync(
        Guid preparationId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty) return null;
        var record = await db.IncrementalTargetPreflights.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        if (record is null) return null;
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleAsync(x => x.Id == preparationId, ct);
        return await BuildViewAsync(record, run, ct);
    }

    public async Task<IReadOnlyList<IncrementalTargetPreflightEvent>> EventsAsync(
        Guid preparationId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty) return [];
        var rows = await db.IncrementalTargetPreflightEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id).Select(x => new IncrementalTargetPreflightEvent(
            x.Id, x.PreparationId, x.ActorId, x.CheckedAtUtc,
            x.EvidenceSha256, x.State, x.Revision)).ToArray();
    }

    private async Task<IncrementalTargetPreflightView> BuildViewAsync(
        IncrementalTargetPreflightRecord row, IncrementalPreparationRecord run,
        CancellationToken ct)
    {
        var probes = await db.IncrementalTargetProbes.AsNoTracking()
            .Where(x => x.PreparationId == row.PreparationId)
            .OrderBy(x => x.SourceRepositoryId)
            .Select(x => new IncrementalTargetProbe(x.SourceRepositoryId,
                x.TargetRepositoryName, x.HistoryStrategy, x.Status))
            .ToArrayAsync(ct);
        if (probes.Length != row.RepositoryCount ||
            probes.Count(x => x.Status == "Exists") != row.VisibleCollisions ||
            probes.Length - row.VisibleCollisions != row.UncertainRepositories ||
            probes.Any(x => !AllowedStatuses.Contains(x.Status)))
            throw new IncrementalTargetPreflightException("TargetPreflightIntegrityFailed");

        var evidence = new IncrementalTargetPreflightEvidence(
            row.PreparationId, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, Guid.Empty, row.Organization,
            row.ReviewSha256, row.SourceValidatedAtUtc,
            row.DestinationValidatedAtUtc, row.CheckedAtUtc,
            row.MembershipStatus, row.MembershipRole,
            row.PrivateRepositoryPolicy, probes);
        if (evidence.EvidenceSha256 != row.EvidenceSha256 ||
            evidence.State != row.State)
            throw new IncrementalTargetPreflightException("TargetPreflightIntegrityFailed");

        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == run.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == run.Id, ct);
        var source = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == run.DestinationConnectionId, ct);
        var age = clock.GetUtcNow() - row.CheckedAtUtc;
        var current = latest == run.SnapshotId &&
            run.State == IncrementalPreparationStates.Prepared &&
            review?.State == IncrementalReviewStates.ApprovedForPlanning &&
            review.DryRunSha256 == row.ReviewSha256 &&
            review.EvidenceSha256 == run.EvidenceSha256 &&
            source?.ValidationCode == "Verified" &&
            destination?.ValidationCode == "Verified" &&
            source.ValidatedAtUtc == row.SourceValidatedAtUtc &&
            destination.ValidatedAtUtc == row.DestinationValidatedAtUtc &&
            review.SourceValidatedAtUtc == row.SourceValidatedAtUtc &&
            review.DestinationValidatedAtUtc == row.DestinationValidatedAtUtc &&
            string.Equals(destination.Organization, row.Organization,
                StringComparison.OrdinalIgnoreCase) &&
            age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5);

        return new(row.PreparationId, run.SnapshotId, run.DestinationConnectionId,
            row.Organization, row.ReviewSha256, row.EvidenceSha256,
            row.CheckedAtUtc, row.Revision, row.State,
            row.MembershipStatus, row.MembershipRole, row.PrivateRepositoryPolicy,
            row.RepositoryCount, row.VisibleCollisions, row.UncertainRepositories,
            row.MembershipStatus == "Active",
            false, false, false, current, probes);
    }
}
