using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain;
using GitHubMigrator.Domain.Tests.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.Ai;

// The templates a pipeline includes, once read, go to the model as data between their own markers, their tasks have to be accounted for like the pipeline's own, and what was sent (and what was not)
// is kept with the proposal.
public sealed class ConversionWithTemplatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static AiConnectionUse Use()
    {
        var connection = new AiConnection(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Corp", AiProvider.OpenAICompatible, "https://llm.corp.example/v1", "gpt-x",
            AiAuthMode.ApiKey, null, TokenLimitParameter.MaxTokens, 4096, 0, 60, 200_000, "ai-connection-x", Now, Now, "Verified", "gpt-x-2025", true, Now, "root");
        return new(connection, connection.ToEndpoint(), "sk-secret-not-sent-in-the-body");
    }

    private static PipelineConverter Converter(FakeChatClient client) => new(client,
        new WorkflowGatePipelineFactory(new FakeToolRunner("actionlint", new(true, "[]", null, 0)), new FakeToolRunner("zizmor", new(true, "[]", null, 0))), new FixedTime(Now));

    private static AzurePipelineSource Pipeline(params AzureServiceReference[] connections) =>
        new(42, "demo", "yaml", "/pipelines/azure-pipelines.yml", "repo", [new("DB_PASSWORD", true, "secrets")], connections, "available");

    private const string Root = "steps:\n- task: UseDotNet@2\n- template: build.yml\n";

    private static TemplateFile Template(string text, string path = "/pipelines/build.yml", string repository = "self", string? @ref = null, string[]? tasks = null, string[]? redactions = null) =>
        new(repository, path, @ref, text, "ab".PadRight(64, 'c'), text.Length, "pipeline", tasks ?? ["DotNetCoreCLI@2"], redactions ?? []);

    private static string Answer(string accounted) => "```yaml\n" + $$$"""
        # azure-tasks: {{{accounted}}}
        name: demo
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
              - name: Build
                env:
                  DB_PASSWORD: ${{ secrets.DB_PASSWORD }}
                run: dotnet build
        """ + "\n```\n\nTriggers are not converted.";

    private static string UserMessage(FakeChatClient client) => client.Calls[0].Request.Messages.Single(m => m.Role == "user").Content;

    [Fact]
    public async Task The_templates_go_to_the_model_between_their_own_markers_after_the_source_and_the_model_is_told_to_account_for_their_tasks()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(900, 300), "gpt-x-2025", 1));
        var templates = new ResolvedTemplates([Template("steps:\n- task: DotNetCoreCLI@2\n", "/pipelines/build.yml"),
            Template("steps:\n- script: echo scan\n", "/steps/security-scan.yml", "Orion Platform/orion-templates", "refs/heads/main", [])], []);

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline(), Root, null, default, templates);

        Assert.Equal(ConversionStatus.ReadyForReview, proposal.Status);
        var user = UserMessage(client);
        var nonce = System.Text.RegularExpressions.Regex.Match(user, @"\[\[SOURCE-([0-9a-f]+)\]\]").Groups[1].Value;
        Assert.Contains($"[[TEMPLATE-{nonce} repository=\"self\" path=\"/pipelines/build.yml\"]]\nsteps:\n- task: DotNetCoreCLI@2\n[[/TEMPLATE-{nonce}]]", user);
        Assert.Contains($"[[TEMPLATE-{nonce} repository=\"Orion Platform/orion-templates\" path=\"/steps/security-scan.yml\" ref=\"refs/heads/main\"]]", user);
        Assert.True(user.IndexOf($"[[/SOURCE-{nonce}]]", StringComparison.Ordinal) < user.IndexOf($"[[TEMPLATE-{nonce}", StringComparison.Ordinal)); // after the source, never inside it
        Assert.Contains("TEMPLATES shown after the source (self:/pipelines/build.yml, Orion Platform/orion-templates:/steps/security-scan.yml)", user);
        Assert.Contains("account for their tasks in the first-line comment too", user);
        Assert.DoesNotContain("TEMPLATES you cannot see", user); // nothing was left out
        Assert.Contains("DotNetCoreCLI@2", user.Split('\n').Single(l => l.StartsWith("AZURE TASKS OF THE SOURCE")));
    }

    [Fact]
    public async Task A_task_that_only_a_template_uses_has_to_be_accounted_for_like_any_other_and_the_stored_proposal_remembers_it()
    {
        var templates = new ResolvedTemplates([Template("steps:\n- task: DotNetCoreCLI@2\n")], []);

        // the model forgets the template's task: the gate says so
        var forgetful = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));
        var blocked = await Converter(forgetful).ConvertAsync(Use(), Pipeline(), Root, new(MaxRepairs: 0), default, templates);
        Assert.Equal(ConversionStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Gates!.Findings, f => f.Code == "TASK_NOT_ACCOUNTED" && f.Message.Contains("DotNetCoreCLI@2"));

        // without the templates that task is not part of the source at all
        var plain = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));
        Assert.Equal(ConversionStatus.ReadyForReview, (await Converter(plain).ConvertAsync(Use(), Pipeline(), Root, new(MaxRepairs: 0), default)).Status);

        var accounted = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(1, 1), "m", 1));
        var ready = await Converter(accounted).ConvertAsync(Use(), Pipeline(), Root, null, default, templates);
        Assert.Equal(["DotNetCoreCLI@2", "UseDotNet@2"], ready.SourceTasks!); // kept, so approving gates the text against the same tasks
    }

    [Fact]
    public async Task What_was_sent_and_what_was_left_out_is_kept_with_the_proposal_without_any_template_text()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(1, 1), "m", 1));
        var template = Template("steps:\n- task: DotNetCoreCLI@2\n", "/pipelines/build.yml", "self", null);
        var templates = new ResolvedTemplates([template], [new("x.yml@nobody", PipelineTemplates.ReasonUnknownAlias)]);

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline(), Root + "- template: x.yml@nobody\n", null, default, templates);

        var provenance = proposal.Provenance!;
        Assert.Equal([new TemplateProvenance("self", "/pipelines/build.yml", null, template.Sha256, template.Bytes)], provenance.Templates);
        Assert.Equal([new UnresolvedTemplate("x.yml@nobody", "UnknownRepositoryAlias")], provenance.UnresolvedTemplates);
        Assert.DoesNotContain("DotNetCoreCLI", System.Text.Json.JsonSerializer.Serialize(provenance.Templates)); // names, hashes and sizes: never the text
        var user = UserMessage(client);
        Assert.Contains("TEMPLATES you cannot see", user);
        Assert.Contains("x.yml@nobody (UnknownRepositoryAlias)", user);
        Assert.Contains("TEMPLATES shown after the source", user);
    }

    [Fact]
    public async Task Without_templates_the_question_is_what_it_always_was_and_the_proposal_has_none()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));
        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline(), Root, null, default);
        var user = UserMessage(client);
        Assert.Contains("TEMPLATES you cannot see (their files are not provided; do not guess their content, mark that work manual in the Notes): build.yml", user);
        Assert.DoesNotContain("[[TEMPLATE-", user);
        Assert.DoesNotContain("TEMPLATES shown", user);
        Assert.Null(proposal.Provenance!.Templates);
        Assert.Null(proposal.Provenance.UnresolvedTemplates);

        // asked for and none are included: nothing is said either
        var none = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2 manual=none"), "stop", new(1, 1), "m", 1));
        await Converter(none).ConvertAsync(Use(), Pipeline(), "steps:\n- task: UseDotNet@2\n", null, default, ResolvedTemplates.None);
        Assert.DoesNotContain("TEMPLATES", UserMessage(none));
    }

    [Fact]
    public async Task What_was_redacted_in_a_template_is_counted_with_the_rest_and_no_secret_reaches_the_model()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(1, 1), "m", 1));
        // the service redacts before it hands the text over: the converter only counts and sends what it was given
        var redacted = SourceRedactor.Redact($"steps:\n- task: DotNetCoreCLI@2\n- script: echo {Token}\n");
        var templates = new ResolvedTemplates([Template(redacted.Text, redactions: redacted.Findings.Select(f => f.Kind).ToArray())], []);
        var rootWithSecret = SourceRedactor.Redact($"{Root}- script: echo {Token}\n");

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline(), $"{Root}- script: echo {Token}\n", null, default, templates);

        Assert.DoesNotContain(Token, string.Join('\n', client.Calls.SelectMany(c => c.Request.Messages.Select(m => m.Content))));
        var total = proposal.Provenance!.Redactions.Sum(r => r.Count);
        Assert.Equal(rootWithSecret.Findings.Count + redacted.Findings.Count, total); // the pipeline's and the template's, together
        Assert.True(redacted.Findings.Count > 0);
    }

    [Fact]
    public void The_service_connections_and_pools_a_template_names_are_the_pipelines_too()
    {
        var connection = new AzureServiceReference("Orion-Azure", "azurerm");
        var pipeline = Pipeline(connection);
        var without = ConversionPlanBuilder.Build(pipeline, Root);
        Assert.Empty(without.Connections);

        var with = ConversionPlanBuilder.Build(pipeline, Root, null, null,
            ["steps:\n- task: AzureCLI@2\n  inputs:\n    azureSubscription: 'Orion-Azure'\n", "pool:\n  name: SelfHosted\n  demands: [docker]\nsteps:\n- script: echo\n"]);
        Assert.Equal(["Orion-Azure"], with.Connections.Select(c => c.Connection));
        Assert.Contains("AZURE_CLIENT_ID", with.Secrets.Select(s => s.Name));
        Assert.Equal(["docker"], with.RunnerLabels);
        Assert.NotEqual(without.Sha256, with.Sha256);

        // not beyond the limit, and a label the pipeline already demanded is not repeated
        var rootWithPool = "pool:\n  name: P\n  demands: [docker]\nsteps:\n- script: echo\n";
        Assert.Equal(["docker", "npm"], ConversionPlanBuilder.Build(Pipeline(), rootWithPool, null, null, ["pool:\n  name: P\n  demands: [docker, npm]\n"]).RunnerLabels);
    }

    [Fact]
    public async Task The_service_connections_and_pools_a_template_names_reach_the_plan_of_the_proposal()
    {
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(1, 1), "m", 1));
        var templates = new ResolvedTemplates([Template("pool:\n  name: P\n  demands: [docker]\nsteps:\n- task: DotNetCoreCLI@2\n  inputs:\n    azureSubscription: 'Orion-Azure'\n")], []);

        var proposal = await Converter(client).ConvertAsync(Use(), Pipeline(new AzureServiceReference("Orion-Azure", "azurerm")), Root, null, default, templates);

        Assert.Equal(["Orion-Azure"], proposal.Plan.Connections.Select(c => c.Connection));
        Assert.Equal(["docker"], proposal.Plan.RunnerLabels);
        Assert.Contains("Orion-Azure (azurerm)", UserMessage(client));
    }

    [Fact]
    public async Task The_nonce_is_chosen_so_that_no_template_text_contains_it()
    {
        var offered = new Queue<string>(["aaaa0000aaaa0000", "bbbb1111bbbb1111"]);
        var client = new FakeChatClient().Then(() => new(Answer("converted=UseDotNet@2,DotNetCoreCLI@2 manual=none"), "stop", new(1, 1), "m", 1));
        var converter = new PipelineConverter(client,
            new WorkflowGatePipelineFactory(new FakeToolRunner("actionlint", new(true, "[]", null, 0)), new FakeToolRunner("zizmor", new(true, "[]", null, 0))), new FixedTime(Now), null, offered.Dequeue);

        await converter.ConvertAsync(Use(), Pipeline(), Root, null, default, new ResolvedTemplates([Template("steps:\n- task: DotNetCoreCLI@2\n# aaaa0000aaaa0000\n")], []));

        var user = UserMessage(client);
        Assert.Contains("[[SOURCE-bbbb1111bbbb1111]]", user);
        Assert.DoesNotContain("[[SOURCE-aaaa0000aaaa0000]]", user);
    }

    [Fact]
    public void A_nonce_that_appears_in_any_text_between_the_markers_is_not_used()
    {
        var offered = new Queue<string>(["aaaa", "bbbb", "cccc"]);
        Assert.Equal("cccc", ConversionPrompt.NewNonceAvoiding(offered.Dequeue, ["text with aaaa in it", "and bbbb here"]));
        var clean = new Queue<string>(["aaaa", "bbbb"]);
        Assert.Equal("aaaa", ConversionPrompt.NewNonceAvoiding(clean.Dequeue, ["nothing", "to see"]));
    }

    [Fact]
    public void The_prompt_header_of_a_template_names_only_a_checked_repository_path_and_ref()
    {
        var (_, user) = ConversionPrompt.Build(ConversionPlanBuilder.Build(Pipeline(), Root), Root, new HashSet<string>(), "n0nce", WorkflowGatePolicy.Default,
            new ResolvedTemplates([Template("steps: []\n", "/a/b.yml", "Orion Platform/orion-templates", "refs/tags/v1")], [new("c.yml", "NotReadable")]));
        Assert.Contains("[[TEMPLATE-n0nce repository=\"Orion Platform/orion-templates\" path=\"/a/b.yml\" ref=\"refs/tags/v1\"]]", user);
        Assert.Contains("c.yml (NotReadable)", user);
        Assert.EndsWith("[[/TEMPLATE-n0nce]]", user.TrimEnd());
    }
}
