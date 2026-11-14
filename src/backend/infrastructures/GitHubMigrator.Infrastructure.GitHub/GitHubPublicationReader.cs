using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Infrastructure.GitHub;

// Reads, with GET requests only and only inside the one destination repository, what is needed to plan putting a single workflow
// file on a new branch: the repository, the base branch tip, the file at the workflow path, the branch to be created and open pull
// requests from it (and, to reconcile an attempt that was made, what became of its branch and pull request). The text of a file is
// compared with an approved hash here and never leaves this class.
public sealed class GitHubPublicationReader(HttpClient client, GitHubEndpoints? endpoints = null) : IGitHubPublicationReader, IGitHubPublicationObserver
{
    private const int MaxResponseBytes = 2_000_000;
    private GitHubEndpoints Endpoints => endpoints ?? GitHubEndpoints.Cloud;

    public string GitUrl(string organization, string repository) => Endpoints.GitUrl(organization, repository);

    public async Task<PublicationTarget> ReadAsync(string organization, string repository, string? baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string? priorWorkflowSha256, string token, CancellationToken ct)
    {
        var root = $"repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}";
        using var repo = await GetAsync(root, token, ct);
        if (repo is null) return new(false, false, null, null, null, WorkflowFileState.Absent, false, null, []);
        var archived = repo.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
        var defaultBranch = Text(repo.RootElement, "default_branch");
        var resolvedBase = baseBranch ?? defaultBranch;
        if (resolvedBase is null) return new(true, archived, defaultBranch, null, null, WorkflowFileState.Absent, false, null, []);

        using var branch = await GetAsync($"{root}/branches/{PathEscape(resolvedBase)}", token, ct);
        var baseSha = branch is null ? null : Text(Property(branch.RootElement, "commit"), "sha");
        if (baseSha is null) return new(true, archived, defaultBranch, resolvedBase, null, WorkflowFileState.Absent, false, null, []);

        var (file, blobSha) = await FileStateAsync($"{root}/contents/{PathEscape(workflowPath)}?ref={baseSha}", workflowSha256, priorWorkflowSha256, requireBlob: true, token, ct);

        using var head = await GetAsync($"{root}/git/ref/heads/{PathEscape(headBranch)}", token, ct);
        var headSha = head is null ? null : Text(Property(head.RootElement, "object"), "sha");

        var pulls = new List<int>();
        using var open = await GetAsync($"{root}/pulls?state=open&head={Uri.EscapeDataString(organization)}:{Uri.EscapeDataString(headBranch)}&per_page=5", token, ct);
        if (open is { RootElement.ValueKind: JsonValueKind.Array })
            foreach (var pull in open.RootElement.EnumerateArray())
                // the filter is the server's job; a server that ignored it must not make every open pull request look like ours
                if (pull.ValueKind == JsonValueKind.Object && Text(Property(pull, "head"), "ref") == headBranch &&
                    pull.TryGetProperty("number", out var n) && n.TryGetInt32(out var number)) pulls.Add(number);
        return new(true, archived, defaultBranch, resolvedBase, baseSha, file, headSha is not null, headSha, pulls, blobSha);
    }

    // The state of the file at a path: the approved text, the text the caller named as replaceable, something else, or nothing. For PriorVersion the blob id the
    // destination reports must be the git id of the bytes that were read; a file whose id cannot be vouched for is "something else", never replaceable.
    private async Task<(WorkflowFileState State, string? BlobSha)> FileStateAsync(string path, string expectedSha256, string? priorSha256, bool requireBlob, string token, CancellationToken ct)
    {
        using var file = await GetAsync(path, token, ct);
        if (file is null) return (WorkflowFileState.Absent, null);
        // a directory (array) or anything that is not a base64 file is "something else is there", never "nothing is there"
        if (file.RootElement.ValueKind != JsonValueKind.Object || Text(file.RootElement, "type") != "file" || Text(file.RootElement, "encoding") != "base64")
            return (WorkflowFileState.Different, null);
        var encoded = Text(file.RootElement, "content");
        if (encoded is null) return (WorkflowFileState.Different, null);
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", ""));
            var hash = WorkflowHash.Sha256(new UTF8Encoding(false, true).GetString(bytes));
            if (hash == expectedSha256) return (WorkflowFileState.Identical, null);
            if (priorSha256 is null || hash != priorSha256) return (WorkflowFileState.Different, null);
            if (!requireBlob) return (WorkflowFileState.PriorVersion, null);
            var blob = Text(file.RootElement, "sha");
            return blob is not null && blob == GitBlob.Sha(bytes) ? (WorkflowFileState.PriorVersion, blob) : (WorkflowFileState.Different, null);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return (WorkflowFileState.Different, null); }
    }

    // ---- reconciliation: what became of an attempt that was made -------------------------------------------------------------------------------

    public async Task<PublicationObservation> ObserveAsync(string organization, string repository, string baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string? replacedWorkflowSha256, int? pullNumber, string token, CancellationToken ct)
    {
        var root = $"repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}";
        using var repo = await GetAsync(root, token, ct);
        if (repo is null) return new(false, false, null, null, [], WorkflowFileState.Absent, WorkflowFileState.Absent);
        var archived = repo.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;

        using var branch = await GetAsync($"{root}/branches/{PathEscape(baseBranch)}", token, ct);
        var baseTip = branch is null ? null : Text(Property(branch.RootElement, "commit"), "sha");
        using var head = await GetAsync($"{root}/git/ref/heads/{PathEscape(headBranch)}", token, ct);
        var headSha = head is null ? null : Text(Property(head.RootElement, "object"), "sha");

        // every state: a pull request that was merged or closed is exactly what this view is for
        var pulls = new List<PullObservation>();
        using (var listed = await GetAsync($"{root}/pulls?state=all&head={Uri.EscapeDataString(organization)}:{Uri.EscapeDataString(headBranch)}&per_page=5", token, ct))
            if (listed is { RootElement.ValueKind: JsonValueKind.Array })
                foreach (var pull in listed.RootElement.EnumerateArray())
                    // the filter is the server's job; a server that ignored it must not make every pull request look like ours
                    if (Pull(pull, headBranch) is { } observed && pulls.All(p => p.Number != observed.Number)) pulls.Add(observed);
        if (pullNumber is { } recorded && pulls.All(p => p.Number != recorded))
        {
            using var direct = await GetAsync($"{root}/pulls/{recorded}", token, ct);
            if (direct is not null && Pull(direct.RootElement, headBranch) is { } found) pulls.Add(found);
        }

        var onHead = headSha is null ? WorkflowFileState.Absent
            : (await FileStateAsync($"{root}/contents/{PathEscape(workflowPath)}?ref={headSha}", workflowSha256, replacedWorkflowSha256, requireBlob: false, token, ct)).State;
        var onBase = baseTip is null ? WorkflowFileState.Absent
            : (await FileStateAsync($"{root}/contents/{PathEscape(workflowPath)}?ref={baseTip}", workflowSha256, replacedWorkflowSha256, requireBlob: false, token, ct)).State;
        return new(true, archived, baseTip, headSha, pulls.OrderByDescending(p => p.Number).ToArray(), onHead, onBase);
    }

    public async Task<PullObservation?> ReadPullAsync(string organization, string repository, int number, string headBranch, string token, CancellationToken ct)
    {
        if (number <= 0) return null;
        using var pull = await GetAsync($"repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}/pulls/{number}", token, ct);
        return pull is null ? null : Pull(pull.RootElement, headBranch);
    }

    // A pull request counts only when its head is the branch asked about and it has a number; `merged` is on a single pull request, `merged_at` also in a list.
    private static PullObservation? Pull(JsonElement pull, string headBranch)
    {
        if (pull.ValueKind != JsonValueKind.Object || Text(Property(pull, "head"), "ref") != headBranch ||
            !pull.TryGetProperty("number", out var n) || !n.TryGetInt32(out var number) || number <= 0) return null;
        var state = Text(pull, "state") is "open" ? "open" : "closed";
        var merged = pull.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True || Text(pull, "merged_at") is { Length: > 0 };
        return new(number, state, merged, Text(Property(pull, "head"), "sha"), Text(Property(pull, "base"), "ref"), Text(pull, "merge_commit_sha"));
    }

    // null means 404; anything else that is not a success is an unreadable target, not an answer.
    private async Task<JsonDocument?> GetAsync(string relative, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoints.Api(relative));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("GitHubMigrator/0.4");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("TargetUnreadable");
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new HttpRequestException("ResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new HttpRequestException("ResponseTooLarge");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct); }
        catch (JsonException) { throw new HttpRequestException("UnexpectedTargetResponse"); }
    }

    private static string PathEscape(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
