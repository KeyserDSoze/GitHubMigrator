using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Infrastructure.GitHub;

// Reads, with GET requests only and only inside the one destination repository, what is needed to plan putting a single workflow
// file on a new branch: the repository, the base branch tip, the file at the workflow path, the branch to be created and open pull
// requests from it. The text of the file is compared with the approved hash here and never leaves this class.
public sealed class GitHubPublicationReader(HttpClient client, GitHubEndpoints? endpoints = null) : IGitHubPublicationReader
{
    private const int MaxResponseBytes = 2_000_000;
    private GitHubEndpoints Endpoints => endpoints ?? GitHubEndpoints.Cloud;

    public string GitUrl(string organization, string repository) => Endpoints.GitUrl(organization, repository);

    public async Task<PublicationTarget> ReadAsync(string organization, string repository, string? baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string token, CancellationToken ct)
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

        var file = await FileStateAsync($"{root}/contents/{PathEscape(workflowPath)}?ref={baseSha}", workflowSha256, token, ct);

        using var head = await GetAsync($"{root}/git/ref/heads/{PathEscape(headBranch)}", token, ct);
        var headSha = head is null ? null : Text(Property(head.RootElement, "object"), "sha");

        var pulls = new List<int>();
        using var open = await GetAsync($"{root}/pulls?state=open&head={Uri.EscapeDataString(organization)}:{Uri.EscapeDataString(headBranch)}&per_page=5", token, ct);
        if (open is { RootElement.ValueKind: JsonValueKind.Array })
            foreach (var pull in open.RootElement.EnumerateArray())
                // the filter is the server's job; a server that ignored it must not make every open pull request look like ours
                if (pull.ValueKind == JsonValueKind.Object && Text(Property(pull, "head"), "ref") == headBranch &&
                    pull.TryGetProperty("number", out var n) && n.TryGetInt32(out var number)) pulls.Add(number);
        return new(true, archived, defaultBranch, resolvedBase, baseSha, file, headSha is not null, headSha, pulls);
    }

    private async Task<WorkflowFileState> FileStateAsync(string path, string expectedSha256, string token, CancellationToken ct)
    {
        using var file = await GetAsync(path, token, ct);
        if (file is null) return WorkflowFileState.Absent;
        // a directory (array) or anything that is not a base64 file is "something else is there", never "nothing is there"
        if (file.RootElement.ValueKind != JsonValueKind.Object || Text(file.RootElement, "type") != "file" || Text(file.RootElement, "encoding") != "base64")
            return WorkflowFileState.Different;
        var encoded = Text(file.RootElement, "content");
        if (encoded is null) return WorkflowFileState.Different;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", "")));
            return WorkflowHash.Sha256(text) == expectedSha256 ? WorkflowFileState.Identical : WorkflowFileState.Different;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return WorkflowFileState.Different; }
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
