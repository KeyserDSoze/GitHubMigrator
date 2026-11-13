using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class RecordingProgress : IPublicationProgress
{
    public List<(string State, string? Commit, int? Pull, string? Url)> Steps { get; } = [];
    public Task RecordAsync(string state, string? commitSha, int? pullNumber, string? pullUrl, CancellationToken ct) { Steps.Add((state, commitSha, pullNumber, pullUrl)); return Task.CompletedTask; }
}

// The writer against scripted GitHub answers: what it sends, in which order, and how it classifies every way a step can go wrong. The principle
// under test: a refusal before any write leaves nothing behind, anything after a write may have left something, and no write is ever repeated.
public sealed class GitHubPublicationWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Pem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
    private const string Yaml = "# azure-tasks: converted=none manual=none\nname: x\non:\n  workflow_dispatch:\njobs: {}\n";
    private static readonly string BaseSha = new('c', 40);
    private static readonly string CommitSha = new('e', 40);
    private const string Path = ".github/workflows/azdo-42-orion-api.yml";
    private const string Head = "migrate/azdo-42-abcd1234";

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static PublicationExecution Plan(Func<PublicationExecution, PublicationExecution>? change = null)
    {
        var plan = new PublicationExecution(Guid.NewGuid(), "target-org", "orion-api", "main", BaseSha, Head, Path, Yaml, WorkflowHash.Sha256(Yaml),
            "ci: add Azure pipeline 42\n\nbody", "Add GitHub Actions workflow for Azure pipeline #42", "## body", new string('d', 64));
        return change is null ? plan : change(plan);
    }

    private static string Blob(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes]));
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => StubHandler.Json(status, JsonSerializer.Serialize(value));
    private static HttpResponseMessage NotFound() => StubHandler.Json(HttpStatusCode.NotFound, "{}");

    private static object Repository(long id = 5, bool archived = false, string full = "target-org/orion-api") =>
        new { id, full_name = full, owner = new { login = full.Split('/')[0] }, archived, disabled = false };

    // ---- the happy sequence, one entry per request, in the order the writer makes them
    private static List<Func<HttpResponseMessage>> Happy() =>
    [
        /* 0 installation  */ () => Json(new { id = 22, app_id = 11, target_type = "Organization", suspended_at = (string?)null, account = new { login = "target-org" },
            permissions = new { contents = "write", pull_requests = "write", workflows = "write", metadata = "read" } }),
        /* 1 mint token    */ () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created),
        /* 2 repository    */ () => Json(Repository()),
        /* 3 base tip      */ () => Json(new { commit = new { sha = BaseSha } }),
        /* 4 path at base  */ NotFound,
        /* 5 head branch   */ NotFound,
        /* 6 create branch */ () => Json(new { @ref = "refs/heads/" + Head, @object = new { sha = BaseSha } }, HttpStatusCode.Created),
        /* 7 put file      */ () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.Created),
        /* 8 open PR       */ () => Json(new { number = 7, html_url = "https://github.com/target-org/orion-api/pull/7" }, HttpStatusCode.Created),
        /* 9 read PR       */ () => Json(new { state = "open", head = new { @ref = Head, sha = CommitSha }, @base = new { @ref = "main" } }),
        /* 10 read file    */ () => Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Yaml)) })
    ];

    private sealed record Run(PublicationOutcome? Outcome, PublicationWriteException? Failure, StubHandler Handler, RecordingProgress Progress)
    {
        public string[] Writes => Handler.Sent.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.Uri.AbsolutePath}").ToArray();
        public string[] Requests => Handler.Sent.Select(r => $"{r.Method} {r.Uri.AbsolutePath}").ToArray();
    }

    // `change` edits the script before it is played; a Func that throws plays a lost connection
    private static async Task<Run> PublishAsync(Action<List<Func<HttpResponseMessage>>>? change = null, PublicationExecution? plan = null, GitHubEndpoints? endpoints = null, CancellationToken ct = default)
    {
        var script = Happy();
        change?.Invoke(script);
        var handler = new StubHandler();
        foreach (var step in script) handler.Then(step);
        var progress = new RecordingProgress();
        var writer = new GitHubPublicationWriter(new HttpClient(handler), new FixedClock(), endpoints);
        try { return new(await writer.PublishAsync(11, 22, Pem, plan ?? Plan(), progress, ct), null, handler, progress); }
        catch (PublicationWriteException ex) { return new(null, ex, handler, progress); }
    }

    private static void Fails(Run run, string code, bool writesPossible)
    {
        Assert.NotNull(run.Failure);
        Assert.Equal((code, writesPossible), (run.Failure.Code, run.Failure.WritesPossible));
    }

    [Fact]
    public async Task It_creates_the_branch_commits_the_one_file_opens_the_pull_request_and_reads_it_all_back()
    {
        var run = await PublishAsync();
        Assert.Equal((CommitSha, 7, "https://github.com/target-org/orion-api/pull/7"), (run.Outcome!.CommitSha, run.Outcome.PullNumber, run.Outcome.PullUrl));
        Assert.Equal(
            ["GET /app/installations/22", "POST /app/installations/22/access_tokens", "GET /repos/target-org/orion-api", "GET /repos/target-org/orion-api/branches/main",
             $"GET /repos/target-org/orion-api/contents/{Path}", $"GET /repos/target-org/orion-api/git/ref/heads/{Head}",
             "POST /repos/target-org/orion-api/git/refs", $"PUT /repos/target-org/orion-api/contents/{Path}", "POST /repos/target-org/orion-api/pulls",
             "GET /repos/target-org/orion-api/pulls/7", $"GET /repos/target-org/orion-api/contents/{Path}"],
            run.Requests.Select(r => r.Replace("%2F", "/")));
        Assert.Equal(["POST /repos/target-org/orion-api/git/refs", $"PUT /repos/target-org/orion-api/contents/{Path}", "POST /repos/target-org/orion-api/pulls"], run.Writes.Skip(1).Select(w => w.Replace("%2F", "/")));

        // the ledger was told before and after each write, one step at a time
        Assert.Equal([PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting, PublicationStates.FileCommitted,
            PublicationStates.PullRequestOpening, PublicationStates.PullRequestOpened], run.Progress.Steps.Select(s => s.State));
        Assert.Equal((7, CommitSha), (run.Progress.Steps[^1].Pull, run.Progress.Steps[^1].Commit));

        // what was sent: the branch at the planned commit, the approved bytes on that branch only, the planned words
        string Body(int index) => run.Handler.Sent[index].Body;
        using (var branch = JsonDocument.Parse(Body(6))) Assert.Equal(("refs/heads/" + Head, BaseSha), (branch.RootElement.GetProperty("ref").GetString(), branch.RootElement.GetProperty("sha").GetString()));
        using (var file = JsonDocument.Parse(Body(7)))
        {
            Assert.Equal(Head, file.RootElement.GetProperty("branch").GetString());
            Assert.Equal(Yaml, Encoding.UTF8.GetString(Convert.FromBase64String(file.RootElement.GetProperty("content").GetString()!)));
            Assert.Equal("ci: add Azure pipeline 42\n\nbody", file.RootElement.GetProperty("message").GetString());
            Assert.False(file.RootElement.TryGetProperty("sha", out _)); // a new file: never an update of an existing one
        }
        using (var pull = JsonDocument.Parse(Body(8)))
            Assert.Equal((Head, "main", "Add GitHub Actions workflow for Azure pipeline #42", "## body"),
                (pull.RootElement.GetProperty("head").GetString(), pull.RootElement.GetProperty("base").GetString(), pull.RootElement.GetProperty("title").GetString(), pull.RootElement.GetProperty("body").GetString()));
        using (var mint = JsonDocument.Parse(Body(1)))
        {
            Assert.Equal(["orion-api"], mint.RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["contents", "pull_requests", "workflows"], mint.RootElement.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());
            Assert.All(mint.RootElement.GetProperty("permissions").EnumerateObject(), p => Assert.Equal("write", p.Value.GetString()));
        }

        // the app signs for the app endpoints, the installation token for the repository ones, and never anything else
        Assert.Equal(2, run.Handler.Sent.Take(2).Count(r => r.Headers["Authorization"].Count(c => c == '.') == 2));
        Assert.All(run.Handler.Sent.Skip(2), r => Assert.Equal("Bearer ghs_0123456789abcdef", r.Headers["Authorization"]));
        // never a merge, an update, a deletion or a force
        Assert.DoesNotContain(run.Requests, r => r.Contains("/merge") || r.StartsWith("PATCH") || r.StartsWith("DELETE"));
    }

    [Fact]
    public async Task A_refusal_of_the_credential_leaves_nothing_behind_and_no_write_is_sent()
    {
        void Case(string name, Action<List<Func<HttpResponseMessage>>> change, string code) => _cases.Add((name, change, code));
        _cases.Clear();
        Case("installation lacks workflows", s => s[0] = () => Json(new { id = 22, app_id = 11, target_type = "Organization", suspended_at = (string?)null, account = new { login = "target-org" },
            permissions = new { contents = "write", pull_requests = "write", metadata = "read" } }), "PublicationInstallationMismatch");
        Case("installation of another org", s => s[0] = () => Json(new { id = 22, app_id = 11, target_type = "Organization", suspended_at = (string?)null, account = new { login = "someone-else" },
            permissions = new { contents = "write", pull_requests = "write", workflows = "write" } }), "PublicationInstallationMismatch");
        Case("installation suspended", s => s[0] = () => Json(new { id = 22, app_id = 11, target_type = "Organization", suspended_at = "2026-01-01T00:00:00Z", account = new { login = "target-org" },
            permissions = new { contents = "write", pull_requests = "write", workflows = "write" } }), "PublicationInstallationMismatch");
        Case("installation is another app's", s => s[0] = () => Json(new { id = 22, app_id = 99, target_type = "Organization", suspended_at = (string?)null, account = new { login = "target-org" },
            permissions = new { contents = "write", pull_requests = "write", workflows = "write" } }), "PublicationInstallationMismatch");
        Case("installation unknown", s => s[0] = NotFound, "PublicationInstallationNotVerified");
        Case("mint refused", s => s[1] = () => Json(new { message = "no" }, HttpStatusCode.UnprocessableEntity), "PublicationMintRejected");
        Case("token has an extra permission", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write", administration = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("token lacks a permission", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("token has only read of contents", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "read", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("token is for all repositories", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "all", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("token covers two repositories", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository(), Repository(6, full: "target-org/other") } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("token covers another repository", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T13:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository(6, full: "target-org/other") } }, HttpStatusCode.Created), "PublicationMintNotRestricted");
        Case("duplicate members are ambiguous", s => s[1] = () => StubHandler.Json(HttpStatusCode.Created,
            "{\"token\":\"ghs_0123456789abcdef\",\"token\":\"ghs_other0123456789\",\"repository_selection\":\"selected\",\"expires_at\":\"2026-11-13T13:00:00Z\"," +
            "\"permissions\":{\"contents\":\"write\",\"metadata\":\"read\",\"pull_requests\":\"write\",\"workflows\":\"write\"},\"repositories\":[{\"id\":5,\"full_name\":\"target-org/orion-api\",\"owner\":{\"login\":\"target-org\"}}]}"),
            "PublicationMintNotRestricted");
        Case("token expires too soon", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-13T12:01:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationExpiryInvalid");
        Case("token lives too long", s => s[1] = () => Json(new { token = "ghs_0123456789abcdef", repository_selection = "selected", expires_at = "2026-11-14T12:00:00Z",
            permissions = new { contents = "write", metadata = "read", pull_requests = "write", workflows = "write" }, repositories = new[] { Repository() } }, HttpStatusCode.Created), "PublicationExpiryInvalid");
        Case("the token cannot see the repository", s => s[2] = NotFound, "PublicationRepositoryNotVisible");
        Case("the repository was archived", s => s[2] = () => Json(Repository(archived: true)), "PublicationRepositoryChanged");
        Case("the repository is another one", s => s[2] = () => Json(Repository(id: 99)), "PublicationRepositoryChanged");
        Case("the app endpoint is unreachable", s => s[0] = () => throw new HttpRequestException("down"), "PublicationInstallationNotVerified");
        Case("the mint endpoint is unreachable", s => s[1] = () => throw new HttpRequestException("down"), "PublicationMintFailed");

        foreach (var (name, change, code) in _cases)
        {
            var run = await PublishAsync(change);
            Fails(run, code, writesPossible: false);
            Assert.True(run.Writes.All(w => w.StartsWith("POST /app/installations/22/access_tokens")), name + ": " + string.Join(", ", run.Writes));
            Assert.Empty(run.Progress.Steps);
        }
    }

    private readonly List<(string Name, Action<List<Func<HttpResponseMessage>>> Change, string Code)> _cases = [];

    [Fact]
    public async Task A_destination_that_moved_or_is_taken_is_refused_before_anything_is_written()
    {
        Fails(await PublishAsync(s => s[3] = () => Json(new { commit = new { sha = new string('7', 40) } })), "PublicationBaseMoved", false);
        Fails(await PublishAsync(s => s[3] = NotFound), "PublicationBaseMoved", false);
        Fails(await PublishAsync(s => s[4] = () => Json(new { type = "file", sha = "x" })), "PublicationWorkflowPathOccupied", false);
        Fails(await PublishAsync(s => s[5] = () => Json(new { @ref = "refs/heads/" + Head })), "PublicationHeadBranchExists", false);
        Fails(await PublishAsync(s => s[3] = () => StubHandler.Json(HttpStatusCode.Forbidden, "{}")), "PublicationReadFailed", false);
        Fails(await PublishAsync(s => s[4] = () => StubHandler.Json(HttpStatusCode.OK, "not json")), "PublicationReadFailed", false);
        foreach (var run in new[] { await PublishAsync(s => s[3] = NotFound), await PublishAsync(s => s[4] = () => Json(new { type = "file" })) })
        {
            Assert.Single(run.Writes); // only the token request
            Assert.Empty(run.Progress.Steps);
        }
    }

    [Fact]
    public async Task A_write_that_is_refused_for_certain_leaves_nothing_but_one_that_may_have_happened_says_so()
    {
        // the branch is the first write: a definite refusal leaves nothing, a 5xx or a lost answer may have left it
        Fails(await PublishAsync(s => s[6] = () => Json(new { message = "Reference already exists" }, HttpStatusCode.UnprocessableEntity)), "PublicationBranchRejected", false);
        Fails(await PublishAsync(s => s[6] = () => Json(new { message = "Resource not accessible by integration" }, HttpStatusCode.Forbidden)), "PublicationBranchRejected", false);
        Fails(await PublishAsync(s => s[6] = () => StubHandler.Json(HttpStatusCode.BadGateway, "{}")), "PublicationBranchFailed", true);
        Fails(await PublishAsync(s => s[6] = () => throw new HttpRequestException("reset")), "PublicationBranchAnswerLost", true);
        Fails(await PublishAsync(s => s[6] = () => throw new TaskCanceledException("timeout")), "PublicationBranchAnswerLost", true);
        Fails(await PublishAsync(s => s[6] = () => StubHandler.Json(HttpStatusCode.Created, "<html>")), "PublicationBranchAnswerUnreadable", true);
        Fails(await PublishAsync(s => s[6] = () => Json(new { @object = new { sha = new string('1', 40) } }, HttpStatusCode.Created)), "PublicationBranchUnexpected", true);

        // once the branch exists everything after it may have left something
        Fails(await PublishAsync(s => s[7] = () => Json(new { message = "workflows permission" }, HttpStatusCode.Forbidden)), "PublicationFileRejected", true);
        Fails(await PublishAsync(s => s[7] = () => StubHandler.Json(HttpStatusCode.InternalServerError, "{}")), "PublicationFileFailed", true);
        Fails(await PublishAsync(s => s[7] = () => throw new HttpRequestException("reset")), "PublicationFileAnswerLost", true);
        Fails(await PublishAsync(s => s[8] = () => Json(new { message = "no commits" }, HttpStatusCode.UnprocessableEntity)), "PublicationPullRequestRejected", true);
        Fails(await PublishAsync(s => s[8] = () => throw new HttpRequestException("reset")), "PublicationPullRequestAnswerLost", true);

        // the lost-answer run: the failing write was sent once and nothing was sent after it
        var run = await PublishAsync(s => s[7] = () => throw new HttpRequestException("reset"));
        Assert.Equal(2, run.Writes.Count(w => w.StartsWith("POST /repos") || w.StartsWith("PUT")));
        Assert.Equal([PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting], run.Progress.Steps.Select(s => s.State));
        Assert.DoesNotContain(run.Requests, r => r.EndsWith("/pulls"));
    }

    [Fact]
    public async Task An_answer_that_is_not_what_was_asked_for_is_not_accepted()
    {
        // the commit must contain exactly the bytes sent, on top of exactly the base commit
        Fails(await PublishAsync(s => s[7] = () => Json(new { content = new { sha = new string('2', 40) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.Created)), "PublicationFileUnexpected", true);
        Fails(await PublishAsync(s => s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = new string('3', 40) } } } }, HttpStatusCode.Created)), "PublicationFileUnexpected", true);
        Fails(await PublishAsync(s => s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha }, new { sha = BaseSha } } } }, HttpStatusCode.Created)), "PublicationFileUnexpected", true);
        Fails(await PublishAsync(s => s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = "not-a-sha", parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.Created)), "PublicationFileUnexpected", true);
        Fails(await PublishAsync(s => s[8] = () => Json(new { html_url = "https://github.com/x" }, HttpStatusCode.Created)), "PublicationPullRequestUnexpected", true);
        Fails(await PublishAsync(s => s[8] = () => Json(new { number = 0 }, HttpStatusCode.Created)), "PublicationPullRequestUnexpected", true);

        // the read-back: open, from our branch at our commit into our base, with the approved text
        Fails(await PublishAsync(s => s[9] = () => Json(new { state = "closed", head = new { @ref = Head, sha = CommitSha }, @base = new { @ref = "main" } })), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = () => Json(new { state = "open", head = new { @ref = "other", sha = CommitSha }, @base = new { @ref = "main" } })), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = () => Json(new { state = "open", head = new { @ref = Head, sha = new string('4', 40) }, @base = new { @ref = "main" } })), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = () => Json(new { state = "open", head = new { @ref = Head, sha = CommitSha }, @base = new { @ref = "develop" } })), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = NotFound), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[10] = () => Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Yaml + "# changed\n")) })), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[10] = NotFound), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[10] = () => throw new HttpRequestException("reset")), "PublicationVerificationFailed", true);
        // a read-back that is refused or garbled is "written, not verified", never "nothing was written": the same plan must not be tried again
        Fails(await PublishAsync(s => s[9] = () => StubHandler.Json(HttpStatusCode.Forbidden, "{}")), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = () => StubHandler.Json(HttpStatusCode.BadGateway, "{}")), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[9] = () => throw new HttpRequestException("reset")), "PublicationVerificationFailed", true);
        Fails(await PublishAsync(s => s[10] = () => StubHandler.Json(HttpStatusCode.OK, "<html>")), "PublicationVerificationFailed", true);
    }

    [Fact]
    public async Task A_link_that_does_not_point_at_the_configured_github_is_not_kept()
    {
        var foreign = await PublishAsync(s => s[8] = () => Json(new { number = 7, html_url = "https://evil.example/target-org/orion-api/pull/7" }, HttpStatusCode.Created));
        Assert.Equal((7, (string?)null), (foreign.Outcome!.PullNumber, foreign.Outcome.PullUrl));
        var none = await PublishAsync(s => s[8] = () => Json(new { number = 7 }, HttpStatusCode.Created));
        Assert.Null(none.Outcome!.PullUrl);

        var enterprise = new GitHubEndpoints("https://ghe.corp.example/api/v3", "https://ghe.corp.example");
        var own = await PublishAsync(s => s[8] = () => Json(new { number = 7, html_url = "https://ghe.corp.example/target-org/orion-api/pull/7" }, HttpStatusCode.Created), endpoints: enterprise);
        Assert.Equal("https://ghe.corp.example/target-org/orion-api/pull/7", own.Outcome!.PullUrl);
        Assert.All(own.Handler.Sent, r => Assert.Equal("ghe.corp.example", r.Uri.Host));
    }

    [Fact]
    public async Task It_writes_only_what_it_was_told_and_only_a_workflow_file_of_the_expected_name_on_a_branch_of_the_expected_shape()
    {
        var bad = new Func<PublicationExecution, PublicationExecution>[]
        {
            p => p with { WorkflowPath = "README.md" },
            p => p with { WorkflowPath = ".github/workflows/../../secrets.yml" },
            p => p with { WorkflowPath = ".github/workflows/azdo-42-x.yaml" },
            p => p with { WorkflowPath = ".github/workflows/Azdo-42-X.yml" },
            p => p with { WorkflowPath = ".github/workflows/deploy.yml" },
            p => p with { HeadBranch = "main" },
            p => p with { HeadBranch = "migrate/../main" },
            p => p with { HeadBranch = "migrate/azdo-42-ABCD1234" },
            p => p with { BaseSha = "abc" },
            p => p with { BaseBranch = "" },
            p => p with { BaseBranch = "../x" },
            p => p with { Organization = "-bad-" },
            p => p with { Repository = ".." },
            p => p with { WorkflowYaml = Yaml + "# not the approved text\n" },
            p => p with { WorkflowYaml = "" },
            p => p with { PullRequestTitle = "" },
            p => p with { PullRequestTitle = new string('t', 300) },
            p => p with { PullRequestBody = new string('b', 70_000) },
            p => p with { CommitMessage = " " }
        };
        foreach (var change in bad)
        {
            var run = await PublishAsync(plan: Plan(change));
            Fails(run, "PublicationRequestInvalid", false);
            Assert.Empty(run.Handler.Sent); // not even a token was asked for
        }
    }

    [Fact]
    public async Task A_cancelled_caller_is_cancelled_not_reported_as_a_refusal_and_inspection_writes_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new StubHandler().Then((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(NotFound()); });
        var writer = new GitHubPublicationWriter(new HttpClient(handler), new FixedClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PublishAsync(11, 22, Pem, Plan(), new RecordingProgress(), cancelled.Token));

        var inspect = new StubHandler();
        foreach (var step in Happy().Take(3)) inspect.Then(step);
        var seen = await new GitHubPublicationWriter(new HttpClient(inspect), new FixedClock()).InspectAsync(11, 22, Pem, "target-org", "orion-api", default);
        Assert.Equal((22L, 5L), (seen.InstallationId, seen.RepositoryId));
        Assert.Equal(["contents:write", "metadata:read", "pull_requests:write", "workflows:write"], seen.Permissions);
        Assert.Equal(3, inspect.Sent.Count);
        Assert.Single(inspect.Sent, r => r.Method == HttpMethod.Post); // the token request, nothing in the repository

        var badKey = await Assert.ThrowsAsync<PublicationWriteException>(() => new GitHubPublicationWriter(new HttpClient(new StubHandler()), new FixedClock()).InspectAsync(11, 22, "not a pem", "target-org", "orion-api", default));
        Assert.Equal(("PublicationSigningKeyInvalid", false), (badKey.Code, badKey.WritesPossible));
    }

    // ---- replacing the file a derived proposal's parent put there ----------------------------------------------------------------------------

    private const string OldYaml = "# azure-tasks: converted=none manual=none\nname: x\non:\n  workflow_dispatch:\njobs: {}\n# the version without triggers\n";

    private static PublicationExecution UpdatePlan(Func<PublicationExecution, PublicationExecution>? change = null) => Plan(p =>
    {
        var update = p with { Mode = PublicationModes.Update, ReplacesBlobSha = Blob(OldYaml), ReplacesWorkflowSha256 = WorkflowHash.Sha256(OldYaml) };
        return change is null ? update : change(update);
    });

    private static HttpResponseMessage OldFile(string? text = null, string? sha = null) => Json(new
    {
        type = "file", encoding = "base64", sha = sha ?? Blob(text ?? OldYaml), content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? OldYaml))
    });

    // the happy script, with the file present at the base commit and GitHub answering 200 (a replaced file) instead of 201 (a created one)
    private static void ToUpdate(List<Func<HttpResponseMessage>> s)
    {
        s[4] = () => OldFile();
        s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task It_replaces_the_named_file_on_the_new_branch_with_the_blob_it_replaces_and_never_touches_the_base_branch()
    {
        var run = await PublishAsync(ToUpdate, UpdatePlan());
        Assert.Equal((CommitSha, 7), (run.Outcome!.CommitSha, run.Outcome.PullNumber));

        // the same requests in the same order as a creation: only the answer to the file write and the file at the base differ
        Assert.Equal(["POST /repos/target-org/orion-api/git/refs", $"PUT /repos/target-org/orion-api/contents/{Path}", "POST /repos/target-org/orion-api/pulls"],
            run.Writes.Skip(1).Select(w => w.Replace("%2F", "/")));
        Assert.Equal([PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting, PublicationStates.FileCommitted,
            PublicationStates.PullRequestOpening, PublicationStates.PullRequestOpened], run.Progress.Steps.Select(s => s.State));

        using (var file = JsonDocument.Parse(run.Handler.Sent[7].Body))
        {
            Assert.Equal(Blob(OldYaml), file.RootElement.GetProperty("sha").GetString()); // GitHub is told which blob is being replaced
            Assert.Equal(Head, file.RootElement.GetProperty("branch").GetString());        // on the new branch, not on main
            Assert.Equal(Yaml, Encoding.UTF8.GetString(Convert.FromBase64String(file.RootElement.GetProperty("content").GetString()!)));
        }
        using (var branch = JsonDocument.Parse(run.Handler.Sent[6].Body)) Assert.Equal(("refs/heads/" + Head, BaseSha), (branch.RootElement.GetProperty("ref").GetString(), branch.RootElement.GetProperty("sha").GetString()));
        // nothing is ever written to the base branch, merged, patched or deleted
        Assert.DoesNotContain(run.Requests, r => r.Contains("/merge") || r.StartsWith("PATCH") || r.StartsWith("DELETE"));
        Assert.DoesNotContain(run.Handler.Sent.Where(r => r.Method != HttpMethod.Get), r => r.Body.Contains("refs/heads/main") || r.Body.Contains("\"branch\":\"main\""));
    }

    [Fact]
    public async Task A_replacement_needs_the_file_at_the_planned_base_to_be_exactly_the_blob_and_the_text_the_plan_names_or_nothing_is_written()
    {
        var gone = await PublishAsync(s => { ToUpdate(s); s[4] = NotFound; }, UpdatePlan());
        Fails(gone, "PublicationWorkflowPathChanged", false);
        var other = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["another blob id"] = () => OldFile(sha: new string('0', 40)),
            ["another text (its own blob id)"] = () => OldFile("name: somebody elses\non: push\njobs: {}\n"),
            ["the text, with a blob id that is not the id of its bytes"] = () => OldFile(sha: Blob(OldYaml + "x")),
            ["a directory"] = () => Json(new[] { new { type = "file" } }),
            ["a submodule"] = () => Json(new { type = "submodule", sha = Blob(OldYaml) }),
            ["not base64"] = () => Json(new { type = "file", encoding = "base64", sha = Blob(OldYaml), content = "!!!not base64!!!" }),
            ["no content"] = () => Json(new { type = "file", encoding = "base64", sha = Blob(OldYaml) }),
            ["bytes that are not text"] = () => Json(new { type = "file", encoding = "base64", sha = Blob(OldYaml), content = Convert.ToBase64String([0xff, 0xfe]) })
        };
        foreach (var (name, answer) in other)
        {
            var run = await PublishAsync(s => { ToUpdate(s); s[4] = answer; }, UpdatePlan());
            Fails(run, "PublicationWorkflowPathChanged", false);
            Assert.True(run.Writes.All(w => w.StartsWith("POST /app/installations/22/access_tokens")), name + ": " + string.Join(", ", run.Writes));
            Assert.Empty(run.Progress.Steps);
        }

        // a creation still needs the path to be empty
        Fails(await PublishAsync(s => s[4] = () => OldFile()), "PublicationWorkflowPathOccupied", false);
    }

    [Fact]
    public async Task Only_the_one_success_a_step_expects_is_believed_so_a_201_for_a_replacement_or_a_200_for_a_creation_is_reported_as_possibly_written()
    {
        var created = await PublishAsync(s => { ToUpdate(s); s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.Created); }, UpdatePlan());
        Fails(created, "PublicationFileFailed", true);
        Assert.Equal([PublicationStates.BranchCreating, PublicationStates.BranchCreated, PublicationStates.FileCommitting], created.Progress.Steps.Select(s => s.State));
        Assert.DoesNotContain(created.Requests, r => r.EndsWith("/pulls")); // no pull request is opened on the strength of an answer that was not expected

        var replaced = await PublishAsync(s => s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }, HttpStatusCode.OK));
        Fails(replaced, "PublicationFileFailed", true);
    }

    [Fact]
    public async Task GitHub_refusing_the_replacement_or_answering_wrongly_is_a_refusal_after_the_branch_exists_and_nothing_is_repeated()
    {
        // 409: the blob id no longer matches on the branch
        var conflict = await PublishAsync(s => { ToUpdate(s); s[7] = () => Json(new { message = "does not match" }, HttpStatusCode.Conflict); }, UpdatePlan());
        Fails(conflict, "PublicationFileRejected", true);
        Assert.Equal(1, conflict.Requests.Count(r => r.StartsWith("PUT ")));

        // a commit that does not sit on the planned base, or a file that is not the new text
        Fails(await PublishAsync(s => { ToUpdate(s); s[7] = () => Json(new { content = new { sha = Blob(Yaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = new string('9', 40) } } } }); }, UpdatePlan()),
            "PublicationFileUnexpected", true);
        Fails(await PublishAsync(s => { ToUpdate(s); s[7] = () => Json(new { content = new { sha = Blob(OldYaml) }, commit = new { sha = CommitSha, parents = new[] { new { sha = BaseSha } } } }); }, UpdatePlan()),
            "PublicationFileUnexpected", true);
        // a lost answer to the write
        var lost = await PublishAsync(s => { ToUpdate(s); s[7] = () => throw new HttpRequestException("reset"); }, UpdatePlan());
        Fails(lost, "PublicationFileAnswerLost", true);
        Assert.Equal(1, lost.Requests.Count(r => r.StartsWith("PUT ")));
    }

    [Fact]
    public async Task A_replacement_is_read_back_like_a_creation_and_a_failed_read_back_is_written_not_verified()
    {
        var wrongFile = await PublishAsync(s => { ToUpdate(s); s[10] = () => Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(OldYaml)) }); }, UpdatePlan());
        Fails(wrongFile, "PublicationVerificationFailed", true); // the pull request exists, but its file is not the approved text
        Assert.Equal(PublicationStates.PullRequestOpened, wrongFile.Progress.Steps[^1].State);
    }

    [Fact]
    public async Task A_plan_that_does_not_say_exactly_which_file_it_replaces_is_refused_before_a_token_is_asked_for()
    {
        var sha256 = WorkflowHash.Sha256(OldYaml);
        var bad = new Func<PublicationExecution, PublicationExecution>[]
        {
            p => p with { Mode = "Delete" },
            p => p with { Mode = "update" },
            p => p with { ReplacesBlobSha = null },                          // an update names the blob...
            p => p with { ReplacesWorkflowSha256 = null },                   // ...and the text
            p => p with { ReplacesBlobSha = "not-a-blob" },
            p => p with { ReplacesBlobSha = new string('B', 40) },
            p => p with { ReplacesWorkflowSha256 = "short" },
            p => p with { ReplacesWorkflowSha256 = p.WorkflowSha256 }        // replacing a file with itself is not a replacement
        };
        foreach (var change in bad)
        {
            var run = await PublishAsync(plan: UpdatePlan(change));
            Fails(run, "PublicationRequestInvalid", false);
            Assert.Empty(run.Handler.Sent);
        }
        // a creation names neither
        Fails(await PublishAsync(plan: Plan(p => p with { ReplacesBlobSha = Blob(OldYaml) })), "PublicationRequestInvalid", false);
        Fails(await PublishAsync(plan: Plan(p => p with { ReplacesWorkflowSha256 = sha256 })), "PublicationRequestInvalid", false);
    }
}
