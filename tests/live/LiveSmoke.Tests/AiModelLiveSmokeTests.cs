using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.Ai;
using GitHubMigrator.Infrastructure.WorkflowTools;
using Xunit;
using Xunit.Abstractions;

namespace GitHubMigrator.LiveSmoke.Tests;

// The product's own chat client against a REAL model endpoint, to turn what the LLM simulator assumes into facts
// (error bodies, token-parameter dialect, how a real answer fares at the gates). It sends only fixed, secret-free
// prompts: a ping, requests that must be rejected, and one small public-style pipeline. It costs a few thousand tokens.
[Collection("live")]
public sealed class AiModelLiveSmokeTests(LiveReport report, ITestOutputHelper output)
{
    private static LiveAi Ai => LiveEnvironment.Ai!;

    private static OpenAiCompatibleChatClient Client() => new(
        new HttpClient(new CompletionOnlyGuardHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })) { Timeout = Timeout.InfiniteTimeSpan });

    private static ChatEndpoint Endpoint(Func<ChatEndpoint, ChatEndpoint>? tweak = null)
    {
        var provider = Enum.Parse<AiProvider>(Ai.Provider, ignoreCase: true);
        var endpoint = new ChatEndpoint(provider, Ai.Endpoint, Ai.Model, Enum.Parse<AiAuthMode>(Ai.Auth, ignoreCase: true), Ai.ApiVersion,
            TimeSpan.FromSeconds(120), 512, 0, MaxInputBytes: 2_000_000, MaxRetries: 1,
            TokenParameter: provider == AiProvider.AzureOpenAI ? TokenLimitParameter.MaxCompletionTokens : TokenLimitParameter.MaxTokens);
        return tweak?.Invoke(endpoint) ?? endpoint;
    }

    private static async Task<(ChatResult? Result, ChatModelException? Failure)> TryAsync(ChatEndpoint endpoint, string? key, ChatRequest request)
    {
        try { return (await Client().CompleteAsync(endpoint, key, request, default), null); }
        catch (ChatModelException ex) { return (null, ex); }
    }

    private static readonly ChatRequest Ping = new([ChatMessage.System("Reply with the single word OK."), ChatMessage.User("ping")], MaxOutputTokens: 512);

    [LiveAiFact]
    public async Task The_verification_ping_is_answered_and_the_token_limit_dialects_are_known()
    {
        var (result, failure) = await TryAsync(Endpoint(), Ai.Key, Ping);
        output.WriteLine(result is null ? $"ping failed: {failure!.Failure} status={failure.HttpStatus} code={failure.ProviderCode}"
            : $"ping ok: model={result.Model} finish={result.FinishReason} usage={result.Usage}");
        Assert.True(result is not null, "The configured model must answer the ping. Failure: " + failure?.Failure + " code=" + failure?.ProviderCode);
        report.Record("ai", "ping", "confirmed",
            $"A {Ai.Provider} endpoint answered the verification ping (finish={result!.FinishReason}, completion tokens={result.Usage?.CompletionTokens}, model reported={result.Model}).");

        var dialects = new List<string>();
        foreach (var parameter in new[] { TokenLimitParameter.MaxTokens, TokenLimitParameter.MaxCompletionTokens })
        {
            var (ok, fail) = await TryAsync(Endpoint(e => e with { TokenParameter = parameter }), Ai.Key, Ping);
            dialects.Add($"{(parameter == TokenLimitParameter.MaxTokens ? "max_tokens" : "max_completion_tokens")}: " +
                (ok is not null ? "accepted" : $"{fail!.Failure} (status {fail.HttpStatus}, code {fail.ProviderCode})"));
        }
        report.Record("ai", "token-limit-dialects", "info", string.Join("; ", dialects) + ". The connection default is max_tokens for OpenAI-compatible and max_completion_tokens for Azure.");
        output.WriteLine(string.Join(" | ", dialects));
    }

    [LiveAiFact]
    public async Task Rejections_are_classified_the_way_the_simulator_assumes()
    {
        if (Ai.Auth == "ApiKey")
        {
            var (_, bad) = await TryAsync(Endpoint(), "definitely-not-a-valid-key-0123456789", Ping);
            output.WriteLine($"bad key -> {bad?.Failure} status={bad?.HttpStatus} code={bad?.ProviderCode}");
            report.Record("ai", "invalid-key", bad?.Failure == ChatFailure.Unauthorized ? "confirmed" : "differs",
                $"A bogus key is classified {bad?.Failure.ToString() ?? "as success"} (status {bad?.HttpStatus}, code {bad?.ProviderCode}); the simulator assumes Unauthorized (401/403).");
        }

        var (_, missing) = await TryAsync(Endpoint(e => e with { Model = "live-smoke-no-such-model" }), Ai.Key, Ping);
        output.WriteLine($"unknown model -> {missing?.Failure} status={missing?.HttpStatus} code={missing?.ProviderCode}");
        report.Record("ai", "unknown-model", missing?.Failure == ChatFailure.ModelNotFound ? "confirmed" : "differs",
            $"An unknown model/deployment is classified {missing?.Failure.ToString() ?? "as success"} (status {missing?.HttpStatus}, code {missing?.ProviderCode}); the simulator assumes ModelNotFound (404).");

        // About 1.2 MB of text exceeds every context window; providers reject it before running the model.
        var huge = new ChatRequest([ChatMessage.User(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 28_000)))], MaxOutputTokens: 16);
        var (accepted, tooLong) = await TryAsync(Endpoint(), Ai.Key, huge);
        output.WriteLine(accepted is not null ? "oversized input was accepted" : $"oversized input -> {tooLong!.Failure} status={tooLong.HttpStatus} code={tooLong.ProviderCode}");
        report.Record("ai", "context-length-exceeded",
            tooLong?.Failure == ChatFailure.ContextLengthExceeded ? "confirmed" : accepted is not null ? "info" : "differs",
            accepted is not null ? "The endpoint accepted ~1.2 MB of input (no context window is enforced; expected for the simulator)."
                : $"An oversized input is classified {tooLong!.Failure} (status {tooLong.HttpStatus}, code {tooLong.ProviderCode}); the simulator assumes ContextLengthExceeded (400 context_length_exceeded or 413).");
    }

    private const string ContractsPipeline = """
        trigger:
        - main
        pool:
          vmImage: ubuntu-latest
        steps:
        - script: dotnet restore
          displayName: dotnet restore
        - script: dotnet build --configuration Release --no-restore
          displayName: dotnet build
        - script: dotnet test --configuration Release --no-build
          displayName: dotnet test
        """;

    private const string DotnetPipeline = """
        trigger:
          branches:
            include: [main]
        variables:
        - group: app-secrets
        pool:
          vmImage: ubuntu-latest
        steps:
        - task: UseDotNet@2
          displayName: Install .NET
          inputs:
            version: 8.x
        - task: DotNetCoreCLI@2
          displayName: Test
          inputs:
            command: test
            projects: tests/**/*.csproj
        - task: PublishTestResults@2
          condition: succeededOrFailed()
          inputs:
            testResultsFormat: VSTest
            testResultsFiles: '**/*.trx'
        - script: ./deploy.sh
          env:
            DB_PASSWORD: $(DB_PASSWORD)
        """;

    private const string DockerPipeline = """
        trigger: none
        pool:
          vmImage: ubuntu-latest
        parameters:
        - name: environment
          type: string
          default: dev
          values: [dev, prod]
        steps:
        - task: Docker@2
          inputs:
            command: buildAndPush
            containerRegistry: sample-acr
            repository: sample/api
            tags: $(Build.BuildId)
        - task: AzureWebApp@1
          inputs:
            azureSubscription: sample-azure
            appType: webAppLinux
            appName: sample-api-${{ parameters.environment }}
            package: $(Pipeline.Workspace)/**/*.zip
        """;

    // The product's own converter (prompt, redaction, gates, one repair) against the real model: how often does a real
    // answer come out approvable, and what stops the others? A measurement, not a pass/fail: it asserts only that the
    // conversion ran. The pipelines are tiny, public-style samples with no secrets.
    [LiveAiFact]
    public async Task The_product_converter_is_measured_on_small_pipelines()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = Enum.Parse<AiProvider>(Ai.Provider, ignoreCase: true);
        var endpoint = Endpoint(e => e with { MaxOutputTokens = 6000, Timeout = TimeSpan.FromSeconds(180) });
        var connection = new AiConnection(Guid.NewGuid(), "live", provider, Ai.Endpoint, Ai.Model, endpoint.Auth, Ai.ApiVersion, endpoint.TokenParameter,
            endpoint.MaxOutputTokens, endpoint.Temperature, 180, endpoint.MaxInputBytes, "", now, now, "Verified", Ai.Model, true, now, "live-smoke");
        var use = new AiConnectionUse(connection, endpoint, Ai.Key);

        var actionlint = ProcessWorkflowToolRunner.Locate("actionlint", LiveEnvironment.Env("GITHUBMIGRATOR_ACTIONLINT"));
        var zizmor = ProcessWorkflowToolRunner.Locate("zizmor", LiveEnvironment.Env("GITHUBMIGRATOR_ZIZMOR"));
        var converter = new PipelineConverter(Client(), new WorkflowGatePipelineFactory(
            actionlint is null ? null : ProcessWorkflowToolRunner.Actionlint(actionlint), zizmor is null ? null : ProcessWorkflowToolRunner.Zizmor(zizmor)), TimeProvider.System);

        var samples = new (string Name, string Yaml, AzurePipelineSource Pipeline)[]
        {
            ("contracts", ContractsPipeline, Source(1, [], [])),
            ("dotnet+group", DotnetPipeline, Source(2, [new("DB_PASSWORD", true, "app-secrets"), new("LOG_LEVEL", false, "app-secrets")], [])),
            ("docker+azure", DockerPipeline, Source(3, [], [new("sample-acr", "dockerregistry"), new("sample-azure", "azurerm")])),
        };
        var ready = 0;
        foreach (var (name, yaml, pipeline) in samples)
        {
            var proposal = await converter.ConvertAsync(use, pipeline, yaml, new(MaxRepairs: 1), default);
            if (proposal.Status == ConversionStatus.ReadyForReview) ready++;
            var blockers = proposal.Gates?.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct().Order().ToArray() ?? [];
            var notRun = proposal.Gates?.Gates.Where(g => g.Status == GateStatus.NotRun).Select(g => g.Gate).ToArray() ?? [];
            var attempts = string.Join(" -> ", proposal.Provenance?.Attempts.Select(a => a.Outcome) ?? []);
            output.WriteLine($"{name}: status={proposal.Status} reason={proposal.Reason} attempts=[{attempts}] blockers=[{string.Join(", ", blockers)}] notRun=[{string.Join(", ", notRun)}]");
            report.Record("ai", "converter-" + name, "info",
                $"status={proposal.Status}, reason={proposal.Reason}, attempts=[{attempts}], blockers=[{string.Join(", ", blockers)}], gates not run=[{string.Join(", ", notRun)}], " +
                $"tokens in/out={proposal.Provenance?.TotalPromptTokens}/{proposal.Provenance?.TotalCompletionTokens}. One sample answer: a measurement, not a pass/fail.");
        }
        report.Record("ai", "converter-summary", "info", $"{ready} of {samples.Length} small sample pipelines reached ReadyForReview with this model (analysers present: {actionlint is not null && zizmor is not null}).");
    }

    private static AzurePipelineSource Source(int id, AzureVariableReference[] variables, AzureServiceReference[] connections) =>
        new(id, "sample-" + id, "yaml", "/azure-pipelines.yml", "repo", variables, connections, "available");
}
