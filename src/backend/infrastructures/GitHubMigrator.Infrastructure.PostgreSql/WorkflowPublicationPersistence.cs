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
    public string Mode { get; set; } = "Create";
    public string? ReplacedBlobSha { get; set; }
    public string? ReplacedWorkflowSha256 { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public Guid? ReleasedById { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseNote { get; set; }
    // what the destination showed when a person released the attempt (json, no workflow text)
    public string? ReleaseEvidence { get; set; }
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
            RequestedById = r.RequestedById, RequestedBy = r.RequestedBy, StartedAtUtc = r.StartedAtUtc, UpdatedAtUtc = r.UpdatedAtUtc,
            Mode = r.Mode, ReplacedBlobSha = r.ReplacedBlobSha, ReplacedWorkflowSha256 = r.ReplacedWorkflowSha256
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

    // Whatever a process left half done and never finished is closed here, as a failure after which writes are possible: nothing says it did not write.
    public async Task<bool> AbandonAsync(Guid id, DateTimeOffset staleBefore, DateTimeOffset at, CancellationToken ct) =>
        await db.WorkflowPublications.Where(x => x.Id == id && x.State != PublicationStates.Published && x.State != PublicationStates.Failed && x.UpdatedAtUtc <= staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedAtState, x => x.State).SetProperty(x => x.State, PublicationStates.Failed)
                .SetProperty(x => x.ErrorCode, "PublicationAbandoned").SetProperty(x => x.WritesPossible, true).SetProperty(x => x.UpdatedAtUtc, at), ct) == 1;

    // The one change a final row accepts (the guard trigger allows nothing else): the release columns go from empty to filled, once.
    public async Task<bool> ReleaseAsync(Guid id, Guid byId, string by, string note, string evidenceJson, DateTimeOffset at, CancellationToken ct)
    {
        var who = by.Length > 160 ? by[..160] : by;
        return await db.WorkflowPublications.Where(x => x.Id == id && x.ReleasedAtUtc == null && (x.State == PublicationStates.Failed || x.State == PublicationStates.Published))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReleasedAtUtc, at).SetProperty(x => x.ReleasedById, byId).SetProperty(x => x.ReleasedBy, who)
                .SetProperty(x => x.ReleaseNote, note).SetProperty(x => x.ReleaseEvidence, evidenceJson), ct) == 1;
    }

    public async Task<PublicationRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.WorkflowPublications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : From(r);
    }

    public async Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct) =>
        (await db.WorkflowPublications.AsNoTracking().Where(x => x.ProposalId == proposalId).OrderByDescending(x => x.StartedAtUtc).Take(50).ToListAsync(ct)).Select(From).ToArray();

    public async Task<IReadOnlyList<PublicationRecord>> ListForPathAsync(string organization, string repository, string workflowPath, CancellationToken ct)
    {
        var org = organization.ToLowerInvariant();
        var repo = repository.ToLowerInvariant();
        return (await db.WorkflowPublications.AsNoTracking()
            .Where(x => x.Organization.ToLower() == org && x.Repository.ToLower() == repo && x.WorkflowPath == workflowPath)
            .OrderByDescending(x => x.StartedAtUtc).Take(20).ToListAsync(ct)).Select(From).ToArray();
    }

    private static PublicationRecord From(WorkflowPublicationRecord r) => new(r.Id, r.ProposalId, r.PlanSha256, r.WorkflowSha256, r.Organization, r.Repository, r.BaseBranch,
        r.BaseSha, r.HeadBranch, r.WorkflowPath, r.State, r.CommitSha, r.PullNumber, r.PullUrl, r.ErrorCode, r.FailedAtState, r.WritesPossible, r.RequestedById, r.RequestedBy,
        r.StartedAtUtc, r.UpdatedAtUtc, r.Mode, r.ReplacedBlobSha, r.ReplacedWorkflowSha256, r.ReleasedAtUtc, r.ReleasedById, r.ReleasedBy, r.ReleaseNote);
}
