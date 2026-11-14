using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// An attempt keeps its exact plan blocked for good: a failure after a write may have left something, an attempt that never finished may have, and a published attempt whose pull request
// was closed unmerged still counts as published. After a person has cleaned up, the plan is free again only because the PRODUCT has looked: at the moment of release it reads the destination
// (GET only, with the destination's read-only credential) and releases the attempt only if nothing of it remains. The release is a mark on the attempt (who, why, what the destination
// showed), written once and enforced by the database; the attempt itself stays on record as it was. The product writes nothing at the destination and never retries on its own.
public sealed class PublicationReleaseService(IPublicationLedger ledger, IConnectionRepository connections, IConnectionSecretStore secrets, IGitHubPublicationObserver observer, TimeProvider clock)
{
    // The writer gives up after two minutes: an attempt that has not moved for this long is not running.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public const int NoteMinLength = 3;
    public const int NoteMaxLength = 500;

    public async Task<PublicationRecord> ReleaseAsync(ProposalActor actor, Guid proposalId, Guid publicationId, Guid destinationId, string? note, CancellationToken ct)
    {
        var clean = CleanNote(note);
        if (clean is null || clean.Length < NoteMinLength) throw new WorkflowPublicationException("NoteRequired");

        var record = await ledger.GetAsync(publicationId, ct);
        if (record is null || record.ProposalId != proposalId) throw new WorkflowPublicationException("PublicationNotFound");
        if (record.ReleasedAtUtc is not null) throw new WorkflowPublicationException("PublicationAlreadyReleased", record: record);
        if (!record.BlocksItsPlan) throw new WorkflowPublicationException("PublicationAlreadyFree", record: record);
        var now = clock.GetUtcNow();
        var finished = record.State is PublicationStates.Published or PublicationStates.Failed;
        // never release what may still be running: a second attempt beside it would write twice
        if (!finished && now - record.UpdatedAtUtc < StaleAfter) throw new WorkflowPublicationException("PublicationStillRunning", record: record);

        var token = await PublicationDestinationAccess.TokenAsync(connections, secrets, destinationId, record.Organization, ct);
        PublicationObservation seen;
        try
        {
            seen = await observer.ObserveAsync(record.Organization, record.Repository, record.BaseBranch, record.HeadBranch, record.WorkflowPath, record.WorkflowSha256,
                record.ReplacedWorkflowSha256, record.PullNumber, token, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { throw new WorkflowPublicationException("DestinationUnreadable", record: record); }

        // what must be gone: the branch, and every pull request from it that is open or was merged. A closed one is inert. A published attempt must be seen closed, not merely unseen.
        var problems = new List<string>();
        if (!seen.RepositoryExists) problems.Add("RepositoryNotFound");
        else
        {
            if (seen.HeadBranchSha is not null) problems.Add("BranchExists");
            if (seen.Pulls.Any(p => p.State == "open")) problems.Add("PullRequestOpen");
            if (seen.Pulls.Any(p => p.Merged)) problems.Add("PullRequestMerged");
            if (record.State == PublicationStates.Published && record.PullNumber is { } recorded && !seen.Pulls.Any(p => p.Number == recorded && p.State == "closed" && !p.Merged))
                problems.Add("PublishedPullRequestNotClosed");
        }
        if (problems.Count > 0) throw new WorkflowPublicationException("PublicationLeftoversExist", problems.Distinct().ToArray(), record);

        if (!finished && !await ledger.AbandonAsync(record.Id, now - StaleAfter, now, ct)) throw new WorkflowPublicationException("PublicationLedgerConflict", record: await ledger.GetAsync(record.Id, ct));
        var evidence = JsonSerializer.Serialize(new
        {
            readAtUtc = now, ledgerState = record.State, ledgerWritesPossible = record.WritesPossible, baseTipSha = seen.BaseTipSha, headBranchExists = false,
            pulls = seen.Pulls.Select(p => new { p.Number, p.State, p.Merged }).Take(10), fileOnBase = seen.FileOnBase.ToString()
        });
        if (!await ledger.ReleaseAsync(record.Id, actor.Id, actor.Name, clean, evidence, now, ct))
            throw new WorkflowPublicationException("PublicationLedgerConflict", record: await ledger.GetAsync(record.Id, ct));
        return (await ledger.GetAsync(record.Id, ct))!;
    }

    private static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var clean = new string(note.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > NoteMaxLength ? clean[..NoteMaxLength] : clean;
    }
}
