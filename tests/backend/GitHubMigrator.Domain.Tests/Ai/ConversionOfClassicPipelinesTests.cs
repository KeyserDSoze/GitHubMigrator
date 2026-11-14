using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Domain.Tests.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.Ai;

// A classic (designer) pipeline reaches the converter as the product's rendering of its definition together with the origin that says so. Only then is it converted; the model is told what it is reading; the tasks in
// it, named or only known by id, are accounted for by the same gate; and the notes the rendering carries are redacted before they are kept.
public sealed class ConversionOfClassicPipelinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";
    private const string Bare = "99999999-0000-0000-0000-000000000001";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static AiConnectionUse Use(bool consent = true)
    {
        var connection = new AiConnection(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Corp", AiProvider.OpenAICompatible, "https://llm.corp.example/v1", "gpt-x",
            AiAuthMode.ApiKey, null, TokenLimitParameter.MaxTokens, 4096, 0, 60, 200_000, "ai-connection-x", Now, Now, "Verified", "gpt-x-2025", consent, Now, "root");
        return new(connection, connection.ToEndpoint(), "sk-secret-not-sent-in-the-body");
    }

    private static PipelineConverter Converter(FakeChatClient client) => new(client,
        new WorkflowGatePipelineFactory(new FakeToolRunner("actionlint", new(true, "[]", null, 0)), new FakeToolRunner("zizmor", new(true, "[]", null, 0))), new FixedTime(Now));

    private static AzurePipelineSource Pipeline(string kind) => new(42, "legacy", kind, null, "repo", [new("DB_PASSWORD", true, "pipeline")], [], null);

    private const string Rendering = """
        # Rendered by GitHubMigrator from a classic (designer) build definition. It is not a file in a repository and it is not meant to be run as it is.
        # What the rendering could not carry over or could not show, then the definition:
        # - No trigger: builds are started by hand.
        steps:
        - task: UseDotNet@2
          displayName: 'Use .NET'
        - task: 99999999-0000-0000-0000-000000000001@3
          displayName: 'Something unnamed'
        """;

    private static string Answer(string header, string extraStep = "") => "```yaml\n" + $$$"""
        # azure-tasks: {{{header}}}
        name: legacy
        on:
          workflow_dispatch:
        permissions:
          contents: read
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@{{{ActionCatalog.All.Single(a => a.Repository == "actions/checkout").Sha}}} # v7.0.1
                with:
                  persist-credentials: false
              - uses: actions/setup-dotnet@{{{ActionCatalog.All.Single(a => a.Repository == "actions/setup-dotnet").Sha}}} # v6.1.0
                with:
                  dotnet-version: 8.x
        {{{extraStep}}}
        """ + "\n```\n\nTriggers are not converted.";

    private static string User(FakeChatClient client) => client.Calls[0].Request.Messages.Single(m => m.Role == "user").Content;

    private static SourceOrigin Classic(params string[] notes) => new(PipelineSourceOrigins.ClassicDefinition, notes);

    [Fact]
    public async Task A_rendering_is_converted_when_it_comes_with_its_origin_and_the_model_is_told_what_it_is_reading()
    {
        var manual = $"      - name: MANUAL PORT REQUIRED {Bare}@3\n        run: echo 'port this by hand'";
        var client = new FakeChatClient().Then(() => new(Answer($"converted=UseDotNet@2 manual={Bare}@3", manual), "stop", new(900, 300), "gpt-x-2025", 1));

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, null, default, null, Classic("No trigger: builds are started by hand."));

        Assert.Equal(ConversionStatus.ReadyForReview, proposal.Status);
        var user = User(client);
        Assert.Contains("THE SOURCE IS A CLASSIC (DESIGNER) BUILD DEFINITION, not a YAML file", user);
        Assert.Contains("a task shown as a bare id could not be named: mark it manual", user);
        Assert.Contains("the workflow only runs by workflow_dispatch", user);
        Assert.Contains("# Rendered by GitHubMigrator from a classic (designer) build definition.", user);
        Assert.Contains(user.Split('\n'), l => l.StartsWith("AZURE TASKS OF THE SOURCE") && l.Contains("UseDotNet@2") && l.Contains($"{Bare}@3"));
        Assert.Equal(["UseDotNet@2", $"{Bare}@3"], proposal.SourceTasks!.OrderByDescending(t => t));
        Assert.Equal(PipelineSourceOrigins.ClassicDefinition, proposal.Provenance!.SourceOrigin);
        Assert.Equal(["No trigger: builds are started by hand."], proposal.Provenance.SourceNotes!);
    }

    [Fact]
    public async Task A_task_the_rendering_only_has_by_id_has_to_be_accounted_for_like_any_other()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, new(MaxRepairs: 0), default, null, Classic());

        Assert.Equal(ConversionStatus.Blocked, proposal.Status);
        Assert.Contains(proposal.Gates!.Findings, f => f.Code == "TASK_NOT_ACCOUNTED" && f.Message.Contains($"{Bare}@3"));
    }

    [Fact]
    public async Task Without_the_origin_a_pipeline_that_is_not_yaml_is_refused_whatever_text_it_comes_with_and_nothing_is_sent()
    {
        var client = new FakeChatClient();
        var refused = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, null, default);
        Assert.Equal((ConversionStatus.NotAttempted, "ClassicPipelineUnsupported"), (refused.Status, refused.Reason));

        // another origin is not this one
        var other = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, null, default, null, new("something-else", []));
        Assert.Equal("ClassicPipelineUnsupported", other.Reason);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public async Task With_the_origin_but_no_text_there_is_nothing_to_convert(string? text)
    {
        var client = new FakeChatClient();
        var refused = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), text, null, default, null, Classic());
        Assert.Equal((ConversionStatus.NotAttempted, "ClassicPipelineUnsupported"), (refused.Status, refused.Reason));
        Assert.Empty(client.Calls);
        Assert.Equal("SourceYamlUnavailable", (await Converter(client).ConvertAsync(Use(), Pipeline("yaml"), text, null, default)).Reason); // a YAML pipeline keeps its own reason
    }

    [Fact]
    public async Task An_origin_does_not_turn_a_yaml_pipeline_into_a_classic_one()
    {
        const string yaml = "steps:\n- task: UseDotNet@2\n";
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline("yaml"), yaml, null, default, null, Classic("a note"));

        Assert.Equal(ConversionStatus.ReadyForReview, proposal.Status);
        Assert.DoesNotContain("CLASSIC (DESIGNER)", User(client));
        Assert.Null(proposal.Provenance!.SourceOrigin);
        Assert.Null(proposal.Provenance.SourceNotes);
    }

    [Fact]
    public async Task The_source_consent_is_still_required_and_nothing_is_sent_without_it()
    {
        var client = new FakeChatClient();
        var refused = await Converter(client).ConvertAsync(Use(consent: false), Pipeline("designerJson"), Rendering, null, default, null, Classic());
        Assert.Equal("SourceConsentRequired", refused.Reason);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task The_notes_are_redacted_before_they_are_kept_and_a_private_key_in_one_is_withheld()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=" + Bare + "@3", $"      - name: MANUAL PORT REQUIRED {Bare}@3\n        run: echo x"), "stop", new(1, 1), "m", 1));
        var notes = new[] { $"Continuous integration trigger, not carried over: branches [+refs/heads/{Token}]", "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Qu\n-----END RSA PRIVATE KEY-----", "a plain note" };

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, null, default, null, Classic(notes));

        var kept = proposal.Provenance!.SourceNotes!;
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(Token, kept[0]);
        Assert.Contains("<REDACTED:", kept[0]);
        Assert.StartsWith("(withheld", kept[1]);
        Assert.DoesNotContain("PRIVATE KEY", string.Join('\n', kept));
        Assert.Equal("a plain note", kept[2]);
    }

    [Fact]
    public async Task No_more_than_forty_notes_are_kept()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=" + Bare + "@3", $"      - name: MANUAL PORT REQUIRED {Bare}@3\n        run: echo x"), "stop", new(1, 1), "m", 1));
        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline("designerJson"), Rendering, null, default, null, Classic(Enumerable.Range(1, 60).Select(i => $"note {i}").ToArray()));
        Assert.Equal(40, proposal.Provenance!.SourceNotes!.Count);
    }
}
