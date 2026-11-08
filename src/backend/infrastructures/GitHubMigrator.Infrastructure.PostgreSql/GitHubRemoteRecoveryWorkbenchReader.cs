using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-088 reads immutable F-082 epochs directly; it does not guess that a
// missing F-086 row means the remote operation was never transmitted.
// The indexed keyset query is bounded; each returned epoch is independently
// revalidated through F-085 (and F-086 when configured).
public sealed class PostgresGitHubRemoteRecoveryWorkbenchReader(
    AppDbContext db,
    GitHubRemoteRecoveryWorkbenchOptions options,
    GitHubRemoteTypedRecoveryBaselineOptions typedOptions,
    GitHubRemoteTransmitHazardOptions markerOptions,
    PostgresGitHubRemotePostSendRecoveryJournal recovery,
    PostgresGitHubRemoteTypedRecoveryBaselineJournal typed)
{
    public async Task<GitHubRemoteRecoveryWorkbenchPage> ListAsync(
        Guid preparationId, Guid planId, Guid? after, int take,
        CancellationToken ct)
    {
        var repo = RequireAllowed();
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            take < 1 || take > 20 || after == Guid.Empty)
            throw Refused("RecoveryWorkbenchQueryInvalid");
        if (!markerOptions.Enabled || markerOptions.AllowedRepositoryId != repo ||
            !string.Equals(markerOptions.AllowedRepository,
                options.AllowedRepository, StringComparison.OrdinalIgnoreCase))
            throw Refused("RecoveryWorkbenchBarrierDisabled");

        var source = from barrier in
                db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            join grant in db.GitHubRemoteTestGrants.AsNoTracking()
                on barrier.GrantId equals grant.Id
            where grant.PreparationId == preparationId &&
                grant.PlanId == planId &&
                barrier.RepositoryId == repo && grant.RepositoryId == repo &&
                grant.Organization + "/" + grant.Repository ==
                    options.AllowedRepository
            select new { Barrier = barrier, Grant = grant };

        if (after is Guid cursorId)
        {
            var cursor = await source
                .Where(x => x.Barrier.Id == cursorId)
                .Select(x => new {
                    x.Barrier.Id, x.Barrier.RecordedAtUtc
                }).SingleOrDefaultAsync(ct);
            if (cursor is null)
                throw Refused("RecoveryWorkbenchCursorInvalid");
            source = source.Where(x =>
                x.Barrier.RecordedAtUtc < cursor.RecordedAtUtc ||
                (x.Barrier.RecordedAtUtc == cursor.RecordedAtUtc &&
                    x.Barrier.Id.CompareTo(cursor.Id) < 0));
        }

        var page = await source
            .OrderByDescending(x => x.Barrier.RecordedAtUtc)
            .ThenByDescending(x => x.Barrier.Id)
            .Take(take + 1)
            .ToListAsync(ct);
        var hasMore = page.Count > take;
        var selected = page.Take(take).ToArray();
        var results = new List<GitHubRemoteRecoveryWorkbenchItem>(
            selected.Length);

        foreach (var epoch in selected)
        {
            // Revalidate the actual immutable F-082/F-055 marker and
            // F-054/F-058/F-085 audits. A forged audit is a hard error,
            // not a misleading "no evidence" dashboard row.
            var post = await recovery.InspectScopedAsync(
                epoch.Barrier.Id, epoch.Barrier.ConfirmationId,
                epoch.Barrier.IntentId, preparationId, planId,
                epoch.Grant.ScopeId, ct);
            if (post is null || post.BarrierId != epoch.Barrier.Id ||
                post.RepositoryId != repo || post.CanDispatch ||
                post.CanRetry || post.MigrationComplete)
                throw Refused("RecoveryWorkbenchHistoryInvalid");

            GitHubRemoteTypedRecoveryBaselineReceipt? snapshot = null;
            if (typedOptions.Enabled &&
                typedOptions.AllowedRepositoryId == repo &&
                string.Equals(typedOptions.AllowedRepository,
                    options.AllowedRepository,
                    StringComparison.OrdinalIgnoreCase))
                snapshot = await typed.ReadAfterBarrierAsync(
                    epoch.Barrier.ConfirmationId,
                    epoch.Barrier.IntentId, preparationId, planId,
                    epoch.Grant.ScopeId, ct);
            var next = GitHubRemoteRecoveryWorkbenchIntegrity.NextAction(
                post.State, snapshot is not null);
            var item = new GitHubRemoteRecoveryWorkbenchItem(
                epoch.Barrier.Id, epoch.Barrier.ConfirmationId,
                epoch.Barrier.IntentId, epoch.Barrier.SessionId,
                preparationId, planId, epoch.Grant.ScopeId,
                epoch.Barrier.HazardId, epoch.Barrier.GrantId, repo,
                epoch.Grant.Organization, epoch.Grant.Repository,
                epoch.Barrier.RecordedAtUtc, post.State,
                next, next is not
                    (GitHubRemoteRecoveryWorkbenchActions.None or
                     GitHubRemoteRecoveryWorkbenchActions.CertifyClosure),
                snapshot is not null, snapshot?.Baseline.Refs.Count,
                snapshot?.CreateCandidates.Count,
                post.BarrierEvidenceSha256, snapshot?.EvidenceSha256,
                post.ReadbackId, post.ReadbackEvidenceSha256,
                post.ReconciliationId, post.ReconciliationEvidenceSha256,
                post.FullObjectClosureSha256,
                post.Checkpoint?.Id, post.Checkpoint?.EvidenceSha256,
                false, false, false, false);
            GitHubRemoteRecoveryWorkbenchIntegrity.RequireSafe(item);
            results.Add(item);
        }
        return new(preparationId, planId, repo, results,
            hasMore ? results[^1].BarrierId : null,
            false, false, false, false);
    }

    private long RequireAllowed()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Refused("RecoveryWorkbenchDisabled");
        return options.AllowedRepositoryId;
    }

    private static GitHubRemoteRecoveryWorkbenchException Refused(string code)
        => new(code);
}
