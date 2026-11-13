using System.Net;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.GitHub;
using static GitHubMigrator.Domain.Tests.Ai.WorkflowProposalServiceTests;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class FakePublicationReader : IGitHubPublicationReader
{
    public PublicationTarget Result { get; set; } = FreshTarget();
    public Exception? Throw { get; set; }
    public List<(string Org, string Repo, string? Base, string Head, string Path, string Sha, string Token)> Calls { get; } = [];

    public static PublicationTarget FreshTarget() => new(true, false, "main", "main", new string('c', 40), WorkflowFileState.Absent, false, null, []);

    public Task<PublicationTarget> ReadAsync(string organization, string repository, string? baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string token, CancellationToken ct)
    {
        Calls.Add((organization, repository, baseBranch, headBranch, workflowPath, workflowSha256, token));
        return Throw is null ? Task.FromResult(Result) : Task.FromException<PublicationTarget>(Throw);
    }

    public string GitUrl(string organization, string repository) => $"https://github.example/{organization}/{repository}.git";
}

internal sealed class FakeDestinations(params MigrationConnection[] items) : IConnectionRepository
{
    public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(items.FirstOrDefault(i => i.Id == id));
    public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task SaveAsync(MigrationConnection connection, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class WorkflowPublicationPlanTests
{
    private const string Token = "pat-token-never-in-a-plan";
    private static readonly Guid DestinationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static MigrationConnection Destination(string provider = ProviderKeys.GitHubEnterpriseCloud, ConnectionDirection direction = ConnectionDirection.Destination,
        string validation = "Verified") => new(DestinationId, "Target", provider, direction, "target-org", "ref-1", Now, Now, validation, "bot", "Verified");

    private sealed record Setup(WorkflowPublicationService Service, FakePublicationReader Reader, WorkflowProposal Proposal, WorkflowProposalService Proposals,
        InMemoryProposalRepository Repo);

    private static async Task<Setup> ApprovedAsync(string? yaml = null, string[]? warnings = null, MigrationConnection? destination = null, string? token = Token, bool approve = true,
        string? name = null, string? notes = null)
    {
        var repo = new InMemoryProposalRepository();
        var proposals = Service(repo);
        var converted = await ConvertedAsync(yaml ?? Clean);
        if (name is not null || notes is not null) converted = converted with { PipelineName = name ?? converted.PipelineName, ModelNotes = notes ?? converted.ModelNotes };
        var kept = (await proposals.RecordAsync(Author, SourceId, "project", 42, AiId, converted, default))!;
        if (approve) await Service(repo, secondApprover: true).ApproveAsync(Reviewer, kept.Id, kept.WorkflowSha256, warnings ?? [], "Reviewed.", default);
        var secrets = new MemorySecrets();
        if (token is not null) secrets.Values["ref-1"] = token;
        var reader = new FakePublicationReader();
        return new(new(proposals, new FakeDestinations(destination ?? Destination()), secrets, reader, new FixedClock()), reader, kept, proposals, repo);
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(2); }

    private static async Task<PublicationPlan> PlanAsync(Setup s, string repository = "orion-api", string? baseBranch = null) =>
        (await s.Service.PlanAsync(s.Proposal.Id, DestinationId, repository, baseBranch, default)).Plan;

    [Fact]
    public async Task A_plan_names_the_path_the_branch_and_the_pull_request_and_reads_the_destination_once()
    {
        var s = await ApprovedAsync();
        var (plan, yaml) = await s.Service.PlanAsync(s.Proposal.Id, DestinationId, "orion-api", null, default);

        Assert.Equal(".github/workflows/azdo-42-orion-api-ci-cd.yml", plan.WorkflowPath);
        Assert.Equal("migrate/azdo-42-" + s.Proposal.WorkflowSha256![..8], plan.HeadBranch);
        Assert.Equal(("target-org", "orion-api", "main"), (plan.Organization, plan.Repository, plan.BaseBranch));
        Assert.Equal(new string('c', 40), plan.BaseSha);
        Assert.True(plan.Ready);
        Assert.Empty(plan.Findings);
        Assert.Equal(Clean, yaml);
        Assert.Equal(s.Proposal.WorkflowSha256, plan.WorkflowSha256);
        Assert.Equal(Now.AddHours(2), plan.ReadAtUtc);
        Assert.Contains("#42", plan.PullRequestTitle);
        Assert.Contains("Orion API CI/CD", plan.PullRequestTitle);
        Assert.Contains(s.Proposal.WorkflowSha256, plan.CommitMessage);

        // what was asked of the destination: the approved hash to compare with, the credential of the destination connection, no base unless given
        var call = Assert.Single(s.Reader.Calls);
        Assert.Equal(("target-org", "orion-api", (string?)null, plan.HeadBranch, plan.WorkflowPath, s.Proposal.WorkflowSha256, Token),
            (call.Org, call.Repo, call.Base, call.Head, call.Path, call.Sha, call.Token));

        var withBase = await s.Service.PlanAsync(s.Proposal.Id, DestinationId, "orion-api", "release/2026", default);
        Assert.Equal("release/2026", s.Reader.Calls[1].Base);
        Assert.Equal("main", withBase.Plan.BaseBranch); // the reader's answer decides what the base is, the request only asks
    }

    [Fact]
    public async Task The_description_says_who_approved_what_and_what_is_not_claimed_and_carries_names_not_values()
    {
        var s = await ApprovedAsync();
        var plan = await PlanAsync(s);
        var body = plan.PullRequestBody;
        Assert.Contains(s.Proposal.WorkflowSha256!, body);
        Assert.Contains("`reviewer`", body);
        Assert.Contains("`author`", body);
        Assert.Contains("`gpt-x`", body);
        Assert.Contains("not** proven equivalent", body);
        Assert.Contains("workflow_dispatch", body);
        Assert.Contains("original Azure triggers were **not** converted", body);
        Assert.Contains("`NUGET_API_KEY`", body);
        Assert.Contains("names only, no value was copied", body);
        Assert.Contains("No task is marked as a manual port.", body);
        Assert.Contains("No warning was accepted at approval.", body);
        Assert.DoesNotContain(Token, System.Text.Json.JsonSerializer.Serialize(plan));
    }

    [Fact]
    public async Task Manual_ports_and_the_warnings_that_were_accepted_are_listed_for_whoever_reads_the_pull_request()
    {
        var manual = Clean.Replace("converted=UseDotNet@2 manual=none", "converted=none manual=UseDotNet@2") + "\n# MANUAL PORT REQUIRED: UseDotNet@2\n";
        var warned = manual.Replace(ActionCatalog.All.Single(a => a.Repository == "actions/setup-dotnet").Sha, new string('a', 40));
        var s = await ApprovedAsync(warned, warnings: ["ACTION_PIN_NOT_VERIFIED"]);
        var body = (await PlanAsync(s)).PullRequestBody;
        Assert.Contains("(`MANUAL PORT REQUIRED` in the file): `UseDotNet@2`", body);
        Assert.Contains("Warnings accepted at approval: `ACTION_PIN_NOT_VERIFIED`", body);
    }

    [Fact]
    public async Task Text_that_came_from_outside_cannot_open_a_heading_a_link_or_a_mention_in_the_description()
    {
        const string hostileName = "x`\n## injected heading @everyone [a](http://evil.example) `y";
        var notes = "step 1\n```\n## fake heading @octocat\n````\n\u001b[31mred\u0000";
        var s = await ApprovedAsync(name: hostileName, notes: notes);
        var plan = await PlanAsync(s);
        var lines = plan.PullRequestBody.Split('\n');

        Assert.DoesNotContain(lines, l => l.StartsWith("## injected", StringComparison.Ordinal));
        Assert.Matches("Converted from the Azure pipeline `[^`\\n]*@everyone[^`\\n]*` \\(#42", plan.PullRequestBody);
        Assert.DoesNotContain('\n', plan.PullRequestTitle);

        // the notes sit in a fence longer than any run of backticks inside them, with control characters removed
        var open = lines.Single(l => l.StartsWith("`````text", StringComparison.Ordinal));
        Assert.Equal(5, open.Length - "text".Length);
        Assert.Contains("`````", lines);
        Assert.Contains("## fake heading @octocat", lines);
        // char checks: a culture-sensitive string search treats control characters as ignorable and would "find" them anywhere
        Assert.DoesNotContain(plan.PullRequestBody, c => c is '\u001b' or '\u0000');

        var long_ = await ApprovedAsync(notes: new string('n', 9000));
        Assert.Single((await PlanAsync(long_)).PullRequestBody.Split('\n'), l => l.Length == 4000); // truncated, not dropped
    }

    [Fact]
    public async Task The_commands_are_quoted_so_a_title_with_a_quote_stays_one_word()
    {
        var s = await ApprovedAsync(name: "it's mine");
        var plan = await PlanAsync(s, "my.repo-1");
        Assert.Equal(7, plan.Steps.Count);
        Assert.Equal(["Clone", "Branch", "Save", "Verify", "Commit", "Push", "OpenPullRequest"], plan.Steps.Select(x => x.Code));
        Assert.Contains("git clone 'https://github.example/target-org/my.repo-1.git' && cd 'my.repo-1'", plan.Steps[0].Command);
        Assert.Contains($"git checkout -b '{plan.HeadBranch}' 'origin/main'", plan.Steps[1].Command);
        Assert.Contains($"sha256sum '{plan.WorkflowPath}'", plan.Steps[3].Command);
        Assert.Contains(s.Proposal.WorkflowSha256!, plan.Steps[3].Command);
        Assert.Contains("workflow", plan.Steps[5].Title); // the permission that a push of a workflow file needs
        Assert.Contains("--title 'Add GitHub Actions workflow for Azure pipeline #42 (it'\\''s mine)'", plan.Steps[6].Command);
        Assert.Contains("gh pr create --base 'main' --head '" + plan.HeadBranch + "'", plan.Steps[6].Command);
        Assert.All(plan.Steps.Where(x => x.Command is not null), x => Assert.DoesNotContain('\n', x.Command!));
        Assert.Contains("git commit -m 'ci: add Azure pipeline 42 as a manual GitHub Actions workflow' -m 'Proposed by an AI model, approved by a person. Workflow SHA-256: ", plan.Steps[4].Command);
    }

    [Fact]
    public async Task Everything_that_stands_in_the_way_is_a_finding_and_a_plan_with_a_blocker_is_not_ready()
    {
        var s = await ApprovedAsync();
        async Task<PublicationPlan> With(PublicationTarget t) { s.Reader.Result = t; s.Reader.Throw = null; return await PlanAsync(s); }
        var fresh = FakePublicationReader.FreshTarget();

        var ready = await With(fresh);
        Assert.True(ready.Ready);

        var missing = await With(fresh with { RepositoryExists = false, BaseBranch = null, BaseSha = null });
        Assert.Equal(["RepositoryNotFound"], missing.Findings.Select(f => f.Code));
        Assert.False(missing.Ready);

        var archived = await With(fresh with { Archived = true });
        Assert.Equal(["RepositoryArchived"], archived.Findings.Select(f => f.Code));
        Assert.False(archived.Ready);

        var noBase = await With(fresh with { BaseSha = null });
        Assert.Equal(["BaseBranchNotFound"], noBase.Findings.Select(f => f.Code));
        Assert.False(noBase.Ready);

        var occupied = await With(fresh with { WorkflowFile = WorkflowFileState.Different });
        Assert.Equal(["WorkflowPathOccupied"], occupied.Findings.Select(f => f.Code));
        Assert.False(occupied.Ready);

        var identical = await With(fresh with { WorkflowFile = WorkflowFileState.Identical });
        Assert.Equal([("AlreadyPublished", "Info")], identical.Findings.Select(f => (f.Code, f.Severity)));
        Assert.False(identical.Ready); // nothing to publish is not "ready to publish"

        var branch = await With(fresh with { HeadBranchExists = true, HeadBranchSha = new string('d', 40) });
        Assert.Equal(["HeadBranchExists"], branch.Findings.Select(f => f.Code));
        Assert.False(branch.Ready);

        var pulls = await With(fresh with { OpenPullRequests = [7, 9] });
        Assert.Equal(["OpenPullRequestExists"], pulls.Findings.Select(f => f.Code));
        Assert.Contains("#7, #9", pulls.Findings[0].Message);
        Assert.False(pulls.Ready);

        var several = await With(fresh with { Archived = true, WorkflowFile = WorkflowFileState.Different, HeadBranchExists = true });
        Assert.Equal(["RepositoryArchived", "WorkflowPathOccupied", "HeadBranchExists"], several.Findings.Select(f => f.Code));
    }

    [Fact]
    public async Task A_destination_that_cannot_be_read_gives_a_plan_that_says_nothing_was_checked()
    {
        var s = await ApprovedAsync();
        foreach (Exception failure in new Exception[] { new HttpRequestException("TargetUnreadable"), new InvalidOperationException("odd") })
        {
            s.Reader.Throw = failure;
            var plan = await PlanAsync(s);
            Assert.Equal(["TargetUnreadable"], plan.Findings.Select(f => f.Code));
            Assert.False(plan.Ready);
            Assert.Null(plan.BaseSha);
        }
        s.Reader.Throw = new OperationCanceledException(); // a cancelled request is not "unreadable"
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanAsync(s));
        s.Reader.Throw = new FormatException("a bug is not a finding");
        await Assert.ThrowsAsync<FormatException>(() => PlanAsync(s));
    }

    [Fact]
    public async Task The_plan_hash_names_the_whole_plan_so_a_change_of_anything_it_depends_on_changes_it()
    {
        var s = await ApprovedAsync();
        var first = await PlanAsync(s);
        Assert.Equal(64, first.PlanSha256.Length);
        Assert.Equal(first.PlanSha256, (await PlanAsync(s)).PlanSha256);

        s.Reader.Result = FakePublicationReader.FreshTarget() with { BaseSha = new string('e', 40) };
        Assert.NotEqual(first.PlanSha256, (await PlanAsync(s)).PlanSha256);
        s.Reader.Result = FakePublicationReader.FreshTarget() with { BaseBranch = "develop" };
        Assert.NotEqual(first.PlanSha256, (await PlanAsync(s)).PlanSha256);
        s.Reader.Result = FakePublicationReader.FreshTarget();
        Assert.NotEqual(first.PlanSha256, (await PlanAsync(s, "another-repo")).PlanSha256);

        var renamed = await ApprovedAsync(name: "A different pipeline name");
        Assert.NotEqual(first.PlanSha256, (await PlanAsync(renamed)).PlanSha256);
    }

    [Fact]
    public async Task Nothing_is_read_from_the_destination_for_a_proposal_nobody_approved_or_that_was_withdrawn()
    {
        var pending = await ApprovedAsync(approve: false);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => PlanAsync(pending))).Code);
        Assert.Empty(pending.Reader.Calls);

        var withdrawn = await ApprovedAsync();
        await withdrawn.Proposals.RevokeAsync(Author, withdrawn.Proposal.Id, "changed my mind", default);
        Assert.Equal("ProposalNotApproved", (await Assert.ThrowsAsync<WorkflowProposalException>(() => PlanAsync(withdrawn))).Code);
        Assert.Empty(withdrawn.Reader.Calls);

        // a tampered approved text is not planned either
        var tampered = await ApprovedAsync();
        tampered.Repo.Rows[tampered.Proposal.Id] = tampered.Repo.Rows[tampered.Proposal.Id] with { WorkflowYaml = Clean + "\n# tampered\n" };
        Assert.Equal("ProposalIntegrityFailure", (await Assert.ThrowsAsync<WorkflowProposalException>(() => PlanAsync(tampered))).Code);
        Assert.Empty(tampered.Reader.Calls);

        Assert.Equal("ProposalNotFound", (await Assert.ThrowsAsync<WorkflowProposalException>(() =>
            pending.Service.PlanAsync(Guid.NewGuid(), DestinationId, "orion-api", null, default))).Code);
    }

    [Fact]
    public async Task The_repository_the_branch_and_the_destination_are_checked_before_the_destination_is_touched()
    {
        var s = await ApprovedAsync();
        foreach (var bad in new[] { "", "..", ".", "a/b", "x y", "x;rm", "ünï", new string('a', 101) })
            Assert.Equal("InvalidRepository", (await Assert.ThrowsAsync<WorkloadInputException>(() => PlanAsync(s, bad))).Code);
        foreach (var bad in new[] { "/x", "x/", "a..b", "x.lock", "a b", "x//y", "x;y", "$(id)", new string('b', 201), "" })
            Assert.Equal("InvalidBranch", (await Assert.ThrowsAsync<WorkloadInputException>(() => PlanAsync(s, "orion-api", bad))).Code);
        Assert.Empty(s.Reader.Calls);

        async Task<string> Refused(Setup setup, Guid? id = null) =>
            (await Assert.ThrowsAsync<WorkloadInputException>(() => setup.Service.PlanAsync(setup.Proposal.Id, id ?? DestinationId, "orion-api", null, default))).Code;
        Assert.Equal("DestinationNotFound", await Refused(s, Guid.NewGuid()));
        Assert.Equal("UnsupportedDestination", await Refused(await ApprovedAsync(destination: Destination(direction: ConnectionDirection.Source))));
        Assert.Equal("UnsupportedDestination", await Refused(await ApprovedAsync(destination: Destination(provider: ProviderKeys.AzureDevOpsServices))));
        Assert.Equal("UnsupportedDestination", await Refused(await ApprovedAsync(destination: Destination(validation: "Unauthorized"))));
        Assert.Equal("DestinationCredentialMissing", await Refused(await ApprovedAsync(token: null)));
        Assert.Equal("DestinationCredentialMissing", await Refused(await ApprovedAsync(token: "")));
    }
}

public sealed class GitHubPublicationReaderTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Workflow = "name: x\non:\n  workflow_dispatch:\njobs: {}\n";
    private static string WorkflowHashOf(string text) => GitHubMigrator.Business.WorkflowGates.WorkflowHash.Sha256(text);

    private static GitHubPublicationReader Reader(StubHandler handler, GitHubEndpoints? endpoints = null) => new(new HttpClient(handler), endpoints);

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => StubHandler.Json(status, JsonSerializer.Serialize(value));
    private static HttpResponseMessage NotFound() => StubHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
    private static HttpResponseMessage Repo(bool archived = false, string branch = "main") => Json(new { archived, default_branch = branch });
    private static HttpResponseMessage BranchTip() => Json(new { commit = new { sha = Sha } });
    private static HttpResponseMessage File(string text, string encoding = "base64") => Json(new
    {
        type = "file", encoding, content = encoding == "base64" ? Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Insert(8, "\n") + "\n" : ""
    });

    private static Task<PublicationTarget> ReadAsync(StubHandler handler, string? baseBranch = null, GitHubEndpoints? endpoints = null) =>
        Reader(handler, endpoints).ReadAsync("target-org", "orion-api", baseBranch, "migrate/azdo-42-abcd1234", ".github/workflows/azdo-42-x.yml", WorkflowHashOf(Workflow), "pat", default);

    [Fact]
    public async Task It_reads_the_repository_the_base_tip_the_file_the_branch_and_the_pull_requests_with_get_requests_only()
    {
        var handler = new StubHandler().Then(() => Repo()).Then(() => BranchTip()).Then(() => NotFound()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()));
        var target = await ReadAsync(handler);

        Assert.Equal((true, false, "main", "main", Sha, WorkflowFileState.Absent, false, null), (target.RepositoryExists, target.Archived, target.DefaultBranch, target.BaseBranch,
            target.BaseSha, target.WorkflowFile, target.HeadBranchExists, target.HeadBranchSha));
        Assert.Empty(target.OpenPullRequests);
        Assert.All(handler.Sent, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("api.github.com", r.Uri.Host);
            Assert.Equal("Bearer pat", r.Headers["Authorization"]);
            Assert.StartsWith("/repos/target-org/orion-api", r.Uri.AbsolutePath);
        });
        Assert.Equal(
            ["/repos/target-org/orion-api", "/repos/target-org/orion-api/branches/main", "/repos/target-org/orion-api/contents/.github/workflows/azdo-42-x.yml",
             "/repos/target-org/orion-api/git/ref/heads/migrate/azdo-42-abcd1234", "/repos/target-org/orion-api/pulls"],
            handler.Sent.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("?ref=" + Sha, handler.Sent[2].Uri.Query); // the file is read at the commit that was planned against, not at a moving branch
        Assert.Equal("?state=open&head=target-org:migrate%2Fazdo-42-abcd1234&per_page=5", handler.Sent[4].Uri.Query);
    }

    [Fact]
    public async Task A_given_base_branch_is_used_and_escaped_and_a_missing_repository_or_base_stops_the_reading()
    {
        var handler = new StubHandler().Then(() => Repo(branch: "trunk")).Then(() => BranchTip()).Then(() => NotFound()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()));
        var target = await ReadAsync(handler, "release/2026 q4");
        Assert.Equal("release/2026 q4", target.BaseBranch);
        Assert.Equal("trunk", target.DefaultBranch);
        Assert.Equal("/repos/target-org/orion-api/branches/release/2026%20q4", handler.Sent[1].Uri.AbsolutePath.Replace("%2F", "/"));

        var noRepo = await ReadAsync(new StubHandler().Then(() => NotFound()));
        Assert.False(noRepo.RepositoryExists);

        var onlyBase = new StubHandler().Then(() => Repo()).Then(() => NotFound());
        var noBase = await ReadAsync(onlyBase);
        Assert.True(noBase.RepositoryExists);
        Assert.Null(noBase.BaseSha);
        Assert.Equal(2, onlyBase.Sent.Count); // nothing else is asked about a base that is not there

        var archived = await ReadAsync(new StubHandler().Then(() => Repo(archived: true)).Then(() => BranchTip()).Then(() => NotFound()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>())));
        Assert.True(archived.Archived);
    }

    [Fact]
    public async Task The_file_at_the_path_is_identical_different_or_absent_and_its_text_never_leaves_the_reader()
    {
        async Task<WorkflowFileState> StateOf(Func<HttpResponseMessage> file) =>
            (await ReadAsync(new StubHandler().Then(() => Repo()).Then(() => BranchTip()).Then(file).Then(() => NotFound()).Then(() => Json(Array.Empty<object>())))).WorkflowFile;

        Assert.Equal(WorkflowFileState.Identical, await StateOf(() => File(Workflow)));
        Assert.Equal(WorkflowFileState.Identical, await StateOf(() => File(Workflow.Replace("\n", "\r\n")))); // the hash ignores line-ending style
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => File(Workflow + "# one more line\n")));
        Assert.Equal(WorkflowFileState.Absent, await StateOf(() => NotFound()));
        // something is there but is not a readable file: never "nothing is there"
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => Json(new[] { new { type = "file" } })));
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => Json(new { type = "dir" })));
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => File(Workflow, encoding: "none")));
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => Json(new { type = "file", encoding = "base64", content = "!!!not base64!!!" })));
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String([0xff, 0xfe, 0xfd]) })));
        Assert.Equal(WorkflowFileState.Different, await StateOf(() => Json(new { type = "file", encoding = "base64" })));
    }

    [Fact]
    public async Task An_existing_branch_and_only_the_pull_requests_that_really_use_it_are_reported()
    {
        var handler = new StubHandler().Then(() => Repo()).Then(() => BranchTip()).Then(() => NotFound())
            .Then(() => Json(new { @ref = "refs/heads/migrate/azdo-42-abcd1234", @object = new { sha = "2222222222222222222222222222222222222222" } }))
            .Then(() => Json(new object[]
            {
                new { number = 7, head = new { @ref = "migrate/azdo-42-abcd1234" } },
                new { number = 8, head = new { @ref = "some-other-branch" } },   // a server that ignored the filter
                new { number = 9, head = new { @ref = "migrate/azdo-42-abcd1234" } },
                new { head = new { @ref = "migrate/azdo-42-abcd1234" } },         // no number
                "junk"
            }));
        var target = await ReadAsync(handler);
        Assert.True(target.HeadBranchExists);
        Assert.Equal("2222222222222222222222222222222222222222", target.HeadBranchSha);
        Assert.Equal([7, 9], target.OpenPullRequests);
    }

    [Fact]
    public async Task Anything_but_an_answer_is_an_unreadable_target_and_the_requests_stay_inside_the_configured_github()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
            await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(new StubHandler().Then(() => StubHandler.Json(status, "{}"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(new StubHandler().Then(() => StubHandler.Json(HttpStatusCode.OK, "not json"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(new StubHandler().Then(() =>
        {
            var big = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 2_100_000) + "{}") };
            return big;
        })));

        var enterprise = new GitHubEndpoints("https://ghe.corp.example/api/v3", "https://ghe.corp.example");
        var handler = new StubHandler().Then(() => NotFound());
        await ReadAsync(handler, endpoints: enterprise);
        Assert.Equal("https://ghe.corp.example/api/v3/repos/target-org/orion-api", handler.Sent[0].Uri.ToString());
        Assert.Equal("https://ghe.corp.example/target-org/orion-api.git", Reader(handler, enterprise).GitUrl("target-org", "orion-api"));
    }
}
