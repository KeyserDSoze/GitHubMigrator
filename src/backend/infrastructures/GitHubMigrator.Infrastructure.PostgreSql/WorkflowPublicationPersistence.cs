using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class WorkflowPublicationRecord
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public string PlanSha256 { get; set; } = "";
    public string WorkflowSha256 { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string WorkflowPath { get; set; } = "";
    public string State { get; set; } = "";
    public string? CommitSha { get; set; }
    public int? PullNumber { get; set; }
    public string? PullUrl { get; set; }
    public string? ErrorCode { get; set; }
    public string? FailedAtState { get; set; }
    public bool WritesPossible { get; set; }
    public Guid RequestedById { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

// The ledger of what the product wrote to a destination. A row is written BEFORE the first write and moves only forward (the schema enforces
// the order, the unique index makes one live attempt per plan, nothing is ever deleted): after a crash or a lost answer it still says how far it got.
public sealed class PostgresPublicationLedger(AppDbContext db) : IPublicationLedger
{
    private const string LiveIndex = "ux_workflow_publications_live";

    public async Task<PublicationRecord?> TryStartAsync(PublicationRecord r, CancellationToken ct)
    {
        db.WorkflowPublications.Add(new WorkflowPublicationRecord
        {
            Id = r.Id, ProposalId = r.ProposalId, PlanSha256 = r.PlanSha256, WorkflowSha256 = r.WorkflowSha256, Organization = r.Organization, Repository = r.Repository,
            BaseBranch = r.BaseBranch, BaseSha = r.BaseSha, HeadBranch = r.HeadBranch, WorkflowPath = r.WorkflowPath, State = r.State, WritesPossible = false,
            RequestedById = r.RequestedById, RequestedBy = r.RequestedBy, StartedAtUtc = r.StartedAtUtc, UpdatedAtUtc = r.UpdatedAtUtc
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: LiveIndex })
        {
            db.ChangeTracker.Clear();
            return null;
        }
        return r;
    }

    public async Task<bool> AdvanceAsync(Guid id, string from, string to, string? commitSha, int? pullNumber, string? pullUrl, DateTimeOffset at, CancellationToken ct) =>
        await db.WorkflowPublications.Where(x => x.Id == id && x.State == from)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, to).SetProperty(x => x.CommitSha, x => commitSha ?? x.CommitSha)
                .SetProperty(x => x.PullNumber, x => pullNumber ?? x.PullNumber).SetProperty(x => x.PullUrl, x => pullUrl ?? x.PullUrl)
                .SetProperty(x => x.UpdatedAtUtc, at), ct) == 1;

    public Task FailAsync(Guid id, string errorCode, bool writesPossible, DateTimeOffset at, CancellationToken ct) =>
        db.WorkflowPublications.Where(x => x.Id == id && x.State != PublicationStates.Published && x.State != PublicationStates.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedAtState, x => x.State).SetProperty(x => x.State, PublicationStates.Failed)
                .SetProperty(x => x.ErrorCode, errorCode).SetProperty(x => x.WritesPossible, writesPossible).SetProperty(x => x.UpdatedAtUtc, at), ct);

    public async Task<PublicationRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.WorkflowPublications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : From(r);
    }

    public async Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct) =>
        (await db.WorkflowPublications.AsNoTracking().Where(x => x.ProposalId == proposalId).OrderByDescending(x => x.StartedAtUtc).Take(50).ToListAsync(ct)).Select(From).ToArray();

    private static PublicationRecord From(WorkflowPublicationRecord r) => new(r.Id, r.ProposalId, r.PlanSha256, r.WorkflowSha256, r.Organization, r.Repository, r.BaseBranch,
        r.BaseSha, r.HeadBranch, r.WorkflowPath, r.State, r.CommitSha, r.PullNumber, r.PullUrl, r.ErrorCode, r.FailedAtState, r.WritesPossible, r.RequestedById, r.RequestedBy,
        r.StartedAtUtc, r.UpdatedAtUtc);
}
