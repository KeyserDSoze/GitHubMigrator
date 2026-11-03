using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-005: drafts are editable, versioned planning inputs; saving never approves or executes migration.
public sealed record MigrationPlanDraft(
    Guid SnapshotId, Guid DestinationConnectionId, string TargetOrganization,
    int Revision, DateTimeOffset UpdatedAtUtc, IReadOnlyList<PlanSelectionInput> Repositories);

public sealed class PlanRevisionConflictException() : Exception("Plan draft was modified concurrently");

public interface IMigrationPlanDraftRepository
{
    Task<MigrationPlanDraft?> GetAsync(Guid snapshotId, Guid destinationConnectionId, CancellationToken ct);
    // expectedRevision=0 is insert-only; higher revisions use atomic compare-and-swap.
    Task<bool> SaveAsync(MigrationPlanDraft draft, int expectedRevision, CancellationToken ct);
}

public sealed class MigrationPlanDraftService(
    MigrationPlanReviewService preview,
    IMigrationPlanDraftRepository drafts,
    TimeProvider clock)
{
    public async Task<MigrationPlanDraft> GetAsync(Guid snapshotId, Guid destinationId, CancellationToken ct)
    {
        // Use the same snapshot/verified-destination checks as preview, even on initial GET.
        var context = await preview.PreviewAsync(snapshotId, destinationId, [], ct);
        var found = await drafts.GetAsync(snapshotId, destinationId, ct);
        if (found is null)
            return new(snapshotId, destinationId, context.TargetOrganization, 0, clock.GetUtcNow(), []);
        if (!string.Equals(found.TargetOrganization, context.TargetOrganization, StringComparison.OrdinalIgnoreCase))
            throw new PlanInputException("PlanDraftDestinationMismatch");
        return found;
    }

    public async Task<MigrationPlanDraft> SaveAsync(Guid snapshotId, Guid destinationId,
        int expectedRevision, IReadOnlyList<PlanSelectionInput>? selections, CancellationToken ct)
    {
        if (expectedRevision is < 0 or int.MaxValue || selections is null)
            throw new PlanInputException("InvalidPlanDraft");
        // Bound untrusted request size BEFORE preview, including potentially pathological regex inputs.
        if (selections.Count > 1_000 ||
            selections.Any(s => s is null || (s.SourceRepositoryId?.Length ?? 0) > 256 ||
                (s.TargetRepositoryName?.Length ?? 0) > 256))
            throw new PlanInputException("PlanDraftTooLarge");
        // Preview checks snapshot scope, strategy and duplicate source IDs. Blocking findings
        // are allowed in drafts, but no approvals can be issued.
        var context = await preview.PreviewAsync(snapshotId, destinationId, selections, ct);
        var next = new MigrationPlanDraft(snapshotId, destinationId, context.TargetOrganization,
            expectedRevision + 1, clock.GetUtcNow(),
            selections.Select(s => s with { TargetRepositoryName = s.TargetRepositoryName?.Trim() ?? "" })
                .OrderBy(s => s.SourceRepositoryId, StringComparer.Ordinal).ToArray());
        if (!await drafts.SaveAsync(next, expectedRevision, ct))
            throw new PlanRevisionConflictException();
        return next;
    }
}
