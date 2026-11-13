using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class WorkflowProposalRecord
{
    public Guid Id { get; set; }
    public Guid SourceConnectionId { get; set; }
    public string ProjectId { get; set; } = "";
    public int PipelineId { get; set; }
    public Guid AiConnectionId { get; set; }
    public string State { get; set; } = "";
    public string? Reason { get; set; }
    public string? WorkflowYaml { get; set; }
    public string? WorkflowSha256 { get; set; }
    public string? ModelNotes { get; set; }
    public string Evidence { get; set; } = "{}";
    public Guid CreatedById { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? ApprovedSha256 { get; set; }
    public string Kind { get; set; } = "Conversion";
    public Guid? ParentProposalId { get; set; }
}

public sealed class WorkflowProposalEventRecord
{
    public long Id { get; set; }
    public Guid ProposalId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? WorkflowSha256 { get; set; }
    public string? Note { get; set; }
    public string? Detail { get; set; }
}

// Append-only history plus one row per proposal whose text never changes (the schema enforces both, see
// WorkflowProposalSchema): the audit of who accepted which text cannot be rewritten through this repository or around it.
public sealed class PostgresWorkflowProposalRepository(AppDbContext db) : IWorkflowProposalRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AddAsync(WorkflowProposal p, ProposalEvent created, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WorkflowProposals.Add(new WorkflowProposalRecord
        {
            Id = p.Id, SourceConnectionId = p.SourceConnectionId, ProjectId = p.ProjectId, PipelineId = p.PipelineId,
            AiConnectionId = p.AiConnectionId, State = p.State.ToString(), Reason = p.Reason, WorkflowYaml = p.WorkflowYaml,
            WorkflowSha256 = p.WorkflowSha256, ModelNotes = p.ModelNotes, Evidence = JsonSerializer.Serialize(p.Evidence, Json),
            CreatedById = p.CreatedById, CreatedBy = p.CreatedBy, CreatedAtUtc = p.CreatedAtUtc, ApprovedSha256 = p.ApprovedSha256,
            Kind = p.Kind, ParentProposalId = p.ParentProposalId
        });
        await db.SaveChangesAsync(ct);
        db.WorkflowProposalEvents.Add(ToRecord(created));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WorkflowProposal?> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.WorkflowProposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : new WorkflowProposal(r.Id, r.SourceConnectionId, r.ProjectId, r.PipelineId, r.AiConnectionId,
            Parse(r.State), r.Reason, r.WorkflowYaml, r.WorkflowSha256, r.ModelNotes,
            JsonSerializer.Deserialize<ProposalEvidence>(r.Evidence, Json) ?? throw new InvalidOperationException("Stored proposal evidence is unreadable."),
            r.CreatedById, r.CreatedBy, r.CreatedAtUtc, r.ApprovedSha256, r.Kind, r.ParentProposalId);
    }

    // No text and no evidence: a list must stay cheap and must not become a second way to read a workflow.
    public async Task<IReadOnlyList<ProposalSummary>> ListAsync(Guid sourceConnectionId, string projectId, int pipelineId, int limit, CancellationToken ct)
    {
        var rows = await db.WorkflowProposals.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId && x.ProjectId == projectId && x.PipelineId == pipelineId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(Math.Clamp(limit, 1, 200))
            .Select(x => new { x.Id, x.State, x.Reason, x.WorkflowSha256, x.CreatedBy, x.CreatedAtUtc, x.AiConnectionId, x.Kind }).ToListAsync(ct);
        return rows.Select(x => new ProposalSummary(x.Id, Parse(x.State), x.Reason, x.WorkflowSha256, x.CreatedBy, x.CreatedAtUtc, x.AiConnectionId, x.Kind)).ToArray();
    }

    public async Task<IReadOnlyList<ProposalEvent>> EventsAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.WorkflowProposalEvents.AsNoTracking().Where(x => x.ProposalId == id).OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Select(x => new ProposalEvent(x.Id, x.ProposalId, x.Type, x.ActorId, x.Actor, x.OccurredAtUtc, x.WorkflowSha256, x.Note,
            x.Detail is null ? null : JsonSerializer.Deserialize<ProposalEventDetail>(x.Detail, Json))).ToArray();
    }

    public async Task<bool> TransitionAsync(Guid id, ProposalState from, ProposalState to, string? approvedSha256, ProposalEvent evt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var fromText = from.ToString();
        var toText = to.ToString();
        // the state in the WHERE clause is the compare-and-swap: the second of two concurrent decisions changes nothing
        var pending = db.WorkflowProposals.Where(x => x.Id == id && x.State == fromText);
        var changed = to == ProposalState.Approved
            ? await pending.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, toText).SetProperty(x => x.ApprovedSha256, approvedSha256), ct)
            : await pending.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, toText), ct);
        if (changed != 1) { await tx.RollbackAsync(ct); return false; }
        db.WorkflowProposalEvents.Add(ToRecord(evt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static WorkflowProposalEventRecord ToRecord(ProposalEvent e) => new()
    {
        ProposalId = e.ProposalId, Type = e.Type, ActorId = e.ActorId, Actor = e.Actor, OccurredAtUtc = e.OccurredAtUtc,
        WorkflowSha256 = e.WorkflowSha256, Note = e.Note, Detail = e.Detail is null ? null : JsonSerializer.Serialize(e.Detail, Json)
    };

    private static ProposalState Parse(string value) =>
        Enum.TryParse<ProposalState>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new InvalidOperationException("Unknown stored proposal state.");
}
