using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-083: server-only lookup. F-082 takes both repository locks, verifies
// all mutable rows again and atomically marks F-055 before transport;
// this baseline lookup deliberately cannot authorize any write.
public sealed class PostgresGitHubSandboxPreSendAuthority(
    AppDbContext db, GitHubRemoteTransmitHazardOptions options,
    PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers)
    : IGitHubSandboxPreSendAuthority
{
    public async Task<string> GetBaselineAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, long repositoryId,
        CancellationToken ct)
    {
        if (!options.Enabled || repositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId ||
            confirmationId == Guid.Empty || intentId == Guid.Empty ||
            preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty)
            throw new GitHubSandboxOneShotException(
                "SandboxSenderAuthorityUnavailable");

        var lineage = await (
            from c in db.GitHubRemoteSandboxDispatchConfirmations.AsNoTracking()
            join g in db.GitHubRemoteTestGrants.AsNoTracking()
                on c.GrantId equals g.Id
            join h in db.GitHubRemoteTransmitHazards.AsNoTracking()
                on c.HazardId equals h.Id
            where c.Id == confirmationId && c.IntentId == intentId &&
                c.RepositoryId == repositoryId &&
                g.RepositoryId == repositoryId &&
                g.PreparationId == preparationId &&
                g.PlanId == planId && g.ScopeId == scopeId &&
                h.RepositoryId == repositoryId &&
                h.GrantId == c.GrantId &&
                h.State == GitHubRemoteTransmitHazardStates.PreparedWriteDisabled &&
                h.MarkedBy == null &&
                h.PacketSha256 == c.PacketSha256 &&
                h.CandidateSetSha256 == c.CandidateSetSha256 &&
                h.TransportAuthorized == false &&
                h.TransportInvoked == false &&
                h.RemoteWritesPerformed == false &&
                h.MayAutomaticallyRetry == false
            select h.BaselineSha256
        ).SingleOrDefaultAsync(ct);

        if (!GitSourceBundleStorageCheck.ValidSha(lineage))
            throw new GitHubSandboxOneShotException(
                "SandboxSenderAuthorityUnavailable");
        return lineage!;
    }

    public Task<GitHubRemoteSandboxPreSendBarrierReceipt> MarkOnceAsync(
        Guid confirmationId, Guid intentId, Guid preparationId,
        Guid planId, Guid scopeId, Guid operatorId, CancellationToken ct) =>
        barriers.RecordPossibleSendAsync(
            confirmationId, intentId, preparationId, planId, scopeId,
            operatorId, ct);
}
