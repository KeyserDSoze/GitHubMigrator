using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.Ai;

// The templates a pipeline includes are source too. They are found in the pipeline's own text, read only from where that text points (this repository, or a `type: git` repository resource of the
// same organization), only as plain YAML paths that never leave the repository, bounded in number, depth and size, redacted one by one, and everything left out is reported with a reason.
public sealed class PipelineTemplatesTests
{
    private sealed class FakeFiles : IRepositoryFileReader
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unreadable { get; } = [];
        public HashSet<string> TooLarge { get; } = [];
        public List<(string Org, string Project, string Repository, string Path, string? Type, string? Version, string Token)> Reads { get; } = [];

        // key: "project|repository|/path"
        public Task<RepositoryFile> ReadRepositoryFileAsync(string organization, string projectId, string repository, string path, string? versionType, string? version, string token, CancellationToken ct)
        {
            Reads.Add((organization, projectId, repository, path, versionType, version, token));
            var key = $"{projectId}|{repository}|{path}";
            if (Unreadable.Contains(key)) throw new HttpRequestException("SourceReadFailed");
            if (TooLarge.Contains(key)) return Task.FromResult(new RepositoryFile(null, true));
            return Task.FromResult(new RepositoryFile(Files.GetValueOrDefault(key), false));
        }
    }

    private static readonly TemplateRoot Root = new("project", "repo-id", "/pipelines/azure-pipelines.yml");

    private static Task<ResolvedTemplates> ResolveAsync(string yaml, FakeFiles files, TemplateRoot? root = null, TemplateLimits? limits = null, bool noReader = false) =>
        PipelineTemplates.ResolveAsync(yaml, root ?? Root, noReader ? null : files, "contoso", "ado-pat", limits ?? new(), default);

    private const string Templated = """
        resources:
          repositories:
          - repository: templates
            type: git
            name: Orion Platform/orion-templates
            ref: refs/heads/main
        steps:
        - template: steps/build.yml
        - template: steps/security-scan.yml@templates
        - script: echo hi
        """;

    // ---- finding them -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_template_value_is_found_in_document_order_once_wherever_it_sits_and_repository_resources_are_read()
    {
        var (references, repositories) = PipelineTemplates.Extract("""
            resources:
              repositories:
              - repository: templates
                type: git
                name: Orion Platform/orion-templates
                ref: refs/heads/main
              - repository: partner
                type: github
                name: partner/shared
              - repository: bad alias!
                name: x/y
            extends:
              template: extends/base.yml@templates
            variables:
            - template: vars/common.yml
            stages:
            - stage: build
              jobs:
              - template: jobs/build.yml
                parameters:
                  steps:
                  - template: nested/step.yml
            steps:
            - template: vars/common.yml
            - template: [not, a, scalar]
            - template:
                nested: mapping
            """);
        Assert.Equal(["extends/base.yml@templates", "vars/common.yml", "jobs/build.yml", "nested/step.yml"], references);
        Assert.Equal(["partner", "templates"], repositories.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("git", "Orion Platform/orion-templates", "refs/heads/main"), (repositories["templates"].Type, repositories["templates"].Name, repositories["templates"].Ref));
        Assert.Equal("github", repositories["partner"].Type);
    }

    [Fact]
    public void Text_that_is_not_a_pipeline_yields_nothing_and_the_number_of_references_is_bounded()
    {
        Assert.Empty(PipelineTemplates.Extract(null).References);
        Assert.Empty(PipelineTemplates.Extract("   ").References);
        Assert.Empty(PipelineTemplates.Extract("a: [unclosed").References);
        Assert.Empty(PipelineTemplates.Extract("a: 1\n---\nb: 2\n").References); // two documents are not a pipeline
        Assert.Empty(PipelineTemplates.Extract(new string('x', 200_001)).References);
        Assert.Empty(PipelineTemplates.Extract(string.Concat(Enumerable.Repeat("- ", 500)) + "template: a.yml").References); // nested far too deep to be a pipeline

        var many = string.Join('\n', Enumerable.Range(1, 80).Select(i => $"- template: t{i}.yml"));
        Assert.Equal(40, PipelineTemplates.Extract("steps:\n" + many).References.Count);
    }

    [Fact]
    public void A_document_nested_beyond_what_a_pipeline_needs_or_with_too_many_nodes_is_read_no_further()
    {
        // 20 levels are read, 60 (below the parser's own limit of 100) are not
        Assert.Equal(["a.yml"], PipelineTemplates.Extract(string.Concat(Enumerable.Repeat("- ", 20)) + "template: a.yml").References);
        Assert.Empty(PipelineTemplates.Extract(string.Concat(Enumerable.Repeat("- ", 60)) + "template: a.yml").References);

        // a template after the first 20,000 nodes is not looked for; one before them is
        var filler = string.Join('\n', Enumerable.Repeat("- a", 25_000));
        Assert.Empty(PipelineTemplates.Extract(filler + "\n- template: late.yml\n").References);
        Assert.Equal(["early.yml"], PipelineTemplates.Extract("- template: early.yml\n" + filler + "\n").References);
    }

    [Fact]
    public void A_repository_resource_without_a_type_is_kept_with_no_type_so_it_is_never_read()
    {
        var (_, repositories) = PipelineTemplates.Extract("resources:\n  repositories:\n  - repository: untyped\n    name: p/r\n  - repository: typed\n    type: git\n    name: p/r\n");
        Assert.Equal("", repositories["untyped"].Type);
        Assert.Equal("git", repositories["typed"].Type);
    }

    // ---- a path, as the pipeline wrote it -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("steps/a.yml", "/pipelines", "/pipelines/steps/a.yml", null)]
    [InlineData("a.yml", "/", "/a.yml", null)]
    [InlineData("../shared/a.yml", "/pipelines/deep", "/pipelines/shared/a.yml", null)]
    [InlineData("./a.yml", "/pipelines", "/pipelines/a.yml", null)]
    [InlineData("/templates/a.yml", "/pipelines/deep", "/templates/a.yml", null)]
    [InlineData("steps\\a.yml", "/p", "/p/steps/a.yml", null)]
    [InlineData("steps/a.yml@templates", "/pipelines/deep", "/steps/a.yml", "templates")]   // with an alias the path is from the root of that repository
    [InlineData("/steps/a.yaml@self", "/x", "/steps/a.yaml", "self")]
    [InlineData("My Steps/a b.YML", "/", "/My Steps/a b.YML", null)]
    public void A_template_path_is_relative_to_the_file_that_includes_it_unless_it_is_rooted_or_names_a_repository(string raw, string directory, string expectedPath, string? expectedAlias)
    {
        var (path, alias, reason) = PipelineTemplates.Parse(raw, directory);
        Assert.Null(reason);
        Assert.Equal((expectedPath, expectedAlias), (path, alias));
    }

    [Theory]
    [InlineData("../a.yml", "/", PipelineTemplates.ReasonEscapes)]
    [InlineData("../../a.yml", "/one", PipelineTemplates.ReasonEscapes)]
    [InlineData("a/../../b.yml", "/", PipelineTemplates.ReasonEscapes)]
    [InlineData("../a.yml@templates", "/x", PipelineTemplates.ReasonEscapes)]
    [InlineData("${{ variables.template }}.yml", "/", PipelineTemplates.ReasonExpression)]
    [InlineData("$(templatePath)/a.yml", "/", PipelineTemplates.ReasonExpression)]
    [InlineData("$[ variables.x ]", "/", PipelineTemplates.ReasonExpression)]
    [InlineData("secrets.txt", "/", PipelineTemplates.ReasonNotYaml)]
    [InlineData("a.yml.exe", "/", PipelineTemplates.ReasonNotYaml)]
    [InlineData("steps/build", "/", PipelineTemplates.ReasonNotYaml)]
    [InlineData("a;b.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("a:b.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("*.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("a%2e%2e/b.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("a\u0000.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("é.yml", "/", PipelineTemplates.ReasonPath)]
    [InlineData("a.yml@bad alias", "/", PipelineTemplates.ReasonPath)]
    [InlineData("", "/", PipelineTemplates.ReasonPath)]
    [InlineData("   ", "/", PipelineTemplates.ReasonPath)]
    [InlineData("/", "/", PipelineTemplates.ReasonPath)]
    public void A_path_that_leaves_the_repository_is_computed_is_not_a_plain_path_or_is_not_yaml_is_never_read(string raw, string directory, string reason)
    {
        var (path, _, why) = PipelineTemplates.Parse(raw, directory);
        Assert.Null(path);
        Assert.Equal(reason, why);
    }

    [Fact]
    public void A_very_long_path_is_not_read()
    {
        Assert.Equal(PipelineTemplates.ReasonPath, PipelineTemplates.Parse(new string('a', 300) + ".yml", "/").Reason);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("", null, null, true)]
    [InlineData("refs/heads/main", "branch", "main", true)]
    [InlineData("refs/heads/release/2026", "branch", "release/2026", true)]
    [InlineData("refs/tags/v1.2", "tag", "v1.2", true)]
    [InlineData("main", "branch", "main", true)]
    [InlineData("refs/pull/1/merge", null, null, false)]
    [InlineData("refs/heads/../x", null, null, false)]
    [InlineData("refs/heads/a/../b", null, null, false)]
    [InlineData("a..b", null, null, false)]
    [InlineData("refs/heads/a//b", null, null, false)]
    [InlineData("refs/heads/a/", null, null, false)]
    [InlineData("-evil", null, null, false)]
    [InlineData("a b", null, null, false)]
    [InlineData("main&path=/etc", null, null, false)]
    public void A_ref_is_a_branch_or_a_tag_with_a_plain_name_and_nothing_else(string? raw, string? type, string? version, bool ok)
    {
        Assert.Equal((type, version, ok), PipelineTemplates.ParseRef(raw));
    }

    [Theory]
    [InlineData("Orion Platform/orion-templates", "Orion Platform", "orion-templates")]
    [InlineData("orion-templates", "project", "orion-templates")]
    [InlineData(" a/b ", "a", "b")]
    public void A_repository_name_is_project_slash_repository_or_a_repository_of_the_pipelines_project(string raw, string project, string repository)
    {
        Assert.Equal((project, repository), PipelineTemplates.ParseRepositoryName(raw, "project"));
    }

    [Theory]
    [InlineData("a/b/c")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a/../b")]
    [InlineData("a/b?x=1")]
    [InlineData("/b")]
    [InlineData("a/")]
    public void Any_other_repository_name_is_not_read(string? raw)
    {
        Assert.Equal((null, null), PipelineTemplates.ParseRepositoryName(raw, "project"));
    }

    // ---- reading them -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_local_template_and_the_one_of_the_repository_resource_are_read_from_where_the_pipeline_points_and_nowhere_else()
    {
        var files = new FakeFiles();
        files.Files["project|repo-id|/pipelines/steps/build.yml"] = "steps:\n- task: DotNetCoreCLI@2\n  inputs:\n    command: build\n";
        files.Files["Orion Platform|orion-templates|/steps/security-scan.yml"] = "steps:\n- task: PublishPipelineArtifact@1\n- script: echo scan\n";

        var resolved = await ResolveAsync(Templated, files);

        Assert.Equal([("contoso", "project", "repo-id", "/pipelines/steps/build.yml", (string?)null, (string?)null, "ado-pat"),
            ("contoso", "Orion Platform", "orion-templates", "/steps/security-scan.yml", "branch", "main", "ado-pat")], files.Reads);
        Assert.Empty(resolved.Unresolved);
        var local = resolved.Files[0];
        Assert.Equal(("self", "/pipelines/steps/build.yml", (string?)null, "pipeline"), (local.Repository, local.Path, local.Ref, local.ReferencedFrom));
        Assert.Equal(["DotNetCoreCLI@2"], local.Tasks);
        var shared = resolved.Files[1];
        Assert.Equal(("Orion Platform/orion-templates", "/steps/security-scan.yml", "refs/heads/main"), (shared.Repository, shared.Path, shared.Ref));
        Assert.Equal(["PublishPipelineArtifact@1"], shared.Tasks);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(shared.Text))), shared.Sha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(shared.Text), shared.Bytes);
    }

    [Fact]
    public async Task A_template_that_includes_another_is_followed_relative_to_its_own_file_and_repository_a_file_is_read_once_and_depth_is_bounded()
    {
        var files = new FakeFiles();
        // the same file spelled three ways is one file; a rooted path is another
        const string yaml = "steps:\n- template: a/one.yml\n- template: ./a/one.yml\n- template: ../pipelines/a/one.yml\n- template: /a/one.yml\n";
        files.Files["project|repo-id|/pipelines/a/one.yml"] = "steps:\n- template: ../b/two.yml\n"; // relative to /pipelines/a
        files.Files["project|repo-id|/pipelines/b/two.yml"] = "steps:\n- template: three.yml\n";
        files.Files["project|repo-id|/pipelines/b/three.yml"] = "steps:\n- template: four.yml\n";
        files.Files["project|repo-id|/pipelines/b/four.yml"] = "steps:\n- script: echo never read\n";
        files.Files["project|repo-id|/a/one.yml"] = "steps:\n- script: echo a rooted path is another file\n";

        var resolved = await ResolveAsync(yaml, files);

        Assert.Equal(["/pipelines/a/one.yml", "/a/one.yml", "/pipelines/b/two.yml", "/pipelines/b/three.yml"], resolved.Files.Select(f => f.Path));
        Assert.Equal(4, files.Reads.Count); // the other mentions of the same file are not read again
        Assert.Equal(["pipeline", "pipeline", "self:/pipelines/a/one.yml", "self:/pipelines/b/two.yml"], resolved.Files.Select(f => f.ReferencedFrom));
        var tooDeep = Assert.Single(resolved.Unresolved);
        Assert.Equal(("four.yml", PipelineTemplates.ReasonTooDeep), (tooDeep.Reference, tooDeep.Reason));
        Assert.DoesNotContain(files.Reads, r => r.Path.EndsWith("four.yml"));
    }

    [Fact]
    public async Task A_file_included_from_two_files_is_read_once()
    {
        var files = new FakeFiles();
        files.Files["project|repo-id|/pipelines/a.yml"] = "steps:\n- template: shared.yml\n";
        files.Files["project|repo-id|/pipelines/b.yml"] = "steps:\n- template: shared.yml\n";
        files.Files["project|repo-id|/pipelines/shared.yml"] = "steps:\n- script: echo once\n";
        var resolved = await ResolveAsync("steps:\n- template: a.yml\n- template: b.yml\n", files);
        Assert.Equal(["/pipelines/a.yml", "/pipelines/b.yml", "/pipelines/shared.yml"], resolved.Files.Select(f => f.Path));
        Assert.Single(files.Reads, r => r.Path == "/pipelines/shared.yml");
    }

    [Fact]
    public async Task A_resource_without_a_type_is_never_read()
    {
        var files = new FakeFiles();
        files.Files["p|r|/x.yml"] = "steps:\n- script: echo\n";
        var resolved = await ResolveAsync("resources:\n  repositories:\n  - repository: untyped\n    name: p/r\nsteps:\n- template: x.yml@untyped\n", files);
        Assert.Empty(files.Reads);
        Assert.Equal([("x.yml@untyped", "RepositoryTypeNotSupported")], resolved.Unresolved.Select(u => (u.Reference, u.Reason)));
    }

    [Fact]
    public async Task A_template_of_another_repository_includes_its_own_files_in_that_repository_not_in_the_pipelines()
    {
        var files = new FakeFiles();
        files.Files["Orion Platform|orion-templates|/steps/security-scan.yml"] = "steps:\n- template: helpers/report.yml\n";
        files.Files["Orion Platform|orion-templates|/steps/helpers/report.yml"] = "steps:\n- script: echo report\n";
        var resolved = await ResolveAsync(Templated.Replace("- template: steps/build.yml\n", ""), files);
        Assert.Equal(["/steps/security-scan.yml", "/steps/helpers/report.yml"], resolved.Files.Select(f => f.Path));
        Assert.All(resolved.Files, f => Assert.Equal("Orion Platform/orion-templates", f.Repository));
        Assert.DoesNotContain(files.Reads, r => r.Repository == "repo-id");
    }

    [Fact]
    public async Task What_cannot_be_read_is_reported_with_a_reason_and_never_guessed()
    {
        var files = new FakeFiles();
        files.Unreadable.Add("project|repo-id|/pipelines/steps/gone.yml");
        files.TooLarge.Add("project|repo-id|/pipelines/steps/huge.yml");
        files.Files["project|repo-id|/pipelines/steps/empty.yml"] = "   ";
        const string yaml = """
            resources:
              repositories:
              - repository: partner
                type: github
                name: partner/shared
              - repository: deep
                type: git
                name: a/b/c
              - repository: odd
                type: git
                name: p/r
                ref: refs/pull/1/merge
            steps:
            - template: steps/gone.yml
            - template: steps/huge.yml
            - template: steps/empty.yml
            - template: x.yml@partner
            - template: x.yml@deep
            - template: x.yml@odd
            - template: x.yml@nobody
            - template: ${{ variables.t }}.yml
            - template: ../../../outside.yml
            - template: notes.txt
            """;
        var resolved = await ResolveAsync(yaml, files);
        Assert.Empty(resolved.Files);
        Assert.Equal([("steps/gone.yml", "NotReadable"), ("steps/huge.yml", "TooLarge"), ("steps/empty.yml", "NotReadable"), ("x.yml@partner", "RepositoryTypeNotSupported"),
            ("x.yml@deep", "RepositoryNameNotSupported"), ("x.yml@odd", "RefNotSupported"), ("x.yml@nobody", "UnknownRepositoryAlias"), ("${{ variables.t }}.yml", "ExpressionPath"),
            ("../../../outside.yml", "EscapesRepository"), ("notes.txt", "NotYaml")], resolved.Unresolved.Select(u => (u.Reference, u.Reason)));
        // only what could be read at all was asked for
        Assert.Equal(["/pipelines/steps/gone.yml", "/pipelines/steps/huge.yml", "/pipelines/steps/empty.yml"], files.Reads.Select(r => r.Path));
    }

    [Fact]
    public async Task Where_the_pipelines_own_file_is_unknown_only_what_does_not_depend_on_it_is_read()
    {
        var files = new FakeFiles();
        files.Files["project|repo-id|/rooted.yml"] = "steps:\n- script: echo rooted\n";
        files.Files["Orion Platform|orion-templates|/steps/security-scan.yml"] = "steps:\n- script: echo scan\n";
        const string yaml = "resources:\n  repositories:\n  - repository: templates\n    type: git\n    name: Orion Platform/orion-templates\nsteps:\n- template: relative.yml\n- template: /rooted.yml\n- template: steps/security-scan.yml@templates\n- template: /self.yml@self\n";
        files.Files["project|repo-id|/self.yml"] = "steps:\n- script: echo self\n";

        var unknownPath = await ResolveAsync(yaml, files, new("project", "repo-id", null));
        Assert.Equal(["/rooted.yml", "/steps/security-scan.yml", "/self.yml"], unknownPath.Files.Select(f => f.Path));
        Assert.Equal([("relative.yml", "RootLocationUnknown")], unknownPath.Unresolved.Select(u => (u.Reference, u.Reason)));

        var unknownRepository = await ResolveAsync(yaml, files, new("project", null, null));
        Assert.Equal(["/steps/security-scan.yml"], unknownRepository.Files.Select(f => f.Path));
        Assert.Equal(["RootLocationUnknown", "RootLocationUnknown", "RootLocationUnknown"], unknownRepository.Unresolved.Select(u => u.Reason));
    }

    [Fact]
    public async Task Without_a_reader_nothing_is_read_and_every_template_says_so()
    {
        var resolved = await ResolveAsync(Templated, new FakeFiles(), noReader: true);
        Assert.Empty(resolved.Files);
        Assert.Equal(["SourceDoesNotReadFiles", "SourceDoesNotReadFiles"], resolved.Unresolved.Select(u => u.Reason));
    }

    [Fact]
    public async Task A_pipeline_without_templates_reads_nothing_and_says_nothing()
    {
        var files = new FakeFiles();
        var resolved = await ResolveAsync("steps:\n- script: echo hi\n", files);
        Assert.True(resolved.IsEmpty);
        Assert.Empty(files.Reads);
    }

    [Fact]
    public async Task The_number_of_files_the_number_of_reads_the_size_of_a_file_and_the_total_are_all_bounded()
    {
        var files = new FakeFiles();
        for (var i = 1; i <= 6; i++) files.Files[$"project|repo-id|/pipelines/t{i}.yml"] = $"steps:\n- script: echo {i}\n";
        var yaml = "steps:\n" + string.Join('\n', Enumerable.Range(1, 6).Select(i => $"- template: t{i}.yml"));

        var capped = await ResolveAsync(yaml, files, limits: new(MaxFiles: 3));
        Assert.Equal(3, capped.Files.Count);
        Assert.Equal(["TooMany", "TooMany", "TooMany"], capped.Unresolved.Select(u => u.Reason));
        Assert.Equal(3, files.Reads.Count);

        files.Reads.Clear();
        var reads = await ResolveAsync(yaml, files, limits: new(MaxReads: 2));
        Assert.Equal(2, files.Reads.Count);
        Assert.Equal(4, reads.Unresolved.Count(u => u.Reason == "TooMany"));

        var big = new FakeFiles();
        big.Files["project|repo-id|/pipelines/big.yml"] = "steps:\n- script: " + new string('x', 5_000) + "\n";
        big.Files["project|repo-id|/pipelines/small.yml"] = "steps:\n- script: echo small\n";
        var sized = await ResolveAsync("steps:\n- template: big.yml\n- template: small.yml\n", big, limits: new(MaxFileChars: 1_000));
        Assert.Equal(["/pipelines/small.yml"], sized.Files.Select(f => f.Path));
        Assert.Equal([("big.yml", "TooLarge")], sized.Unresolved.Select(u => (u.Reference, u.Reason)));

        var budget = await ResolveAsync("steps:\n- template: big.yml\n- template: small.yml\n", big, limits: new(MaxTotalBytes: 5_030));
        Assert.Equal(["/pipelines/big.yml"], budget.Files.Select(f => f.Path));
        Assert.Equal([("small.yml", "BudgetExceeded")], budget.Unresolved.Select(u => (u.Reference, u.Reason)));
    }

    [Fact]
    public async Task A_template_is_redacted_before_it_is_kept_and_one_that_cannot_be_redacted_is_not_sent()
    {
        var files = new FakeFiles();
        const string token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";
        files.Files["project|repo-id|/pipelines/a.yml"] = $"steps:\n- script: curl -H 'Authorization: token {token}' https://example.invalid\n- task: Docker@2\n";
        files.Files["project|repo-id|/pipelines/key.yml"] = "steps:\n- script: |\n    echo '-----BEGIN RSA PRIVATE KEY-----\n    MIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Qu\n    -----END RSA PRIVATE KEY-----'\n";
        var resolved = await ResolveAsync("steps:\n- template: a.yml\n- template: key.yml\n", files);

        var kept = Assert.Single(resolved.Files);
        Assert.DoesNotContain(token, kept.Text);
        Assert.Contains("<REDACTED:", kept.Text);
        Assert.NotEmpty(kept.RedactionKinds);
        Assert.Equal(["Docker@2"], kept.Tasks);
        Assert.Equal([("key.yml", "RedactionBlocked")], resolved.Unresolved.Select(u => (u.Reference, u.Reason)));
        Assert.DoesNotContain("PRIVATE KEY", resolved.Files.Single().Text);
    }

    [Fact]
    public async Task A_cancellation_is_not_swallowed_and_a_reference_shown_back_is_one_printable_line()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipelineTemplates.ResolveAsync(Templated, Root, new FakeFiles(), "contoso", "t", new(), source.Token));

        var resolved = await ResolveAsync("steps:\n- template: \"a\\nb\\u0007.txt\"\n- template: \"" + new string('z', 300) + ".txt\"\n", new FakeFiles());
        Assert.All(resolved.Unresolved, u => { Assert.DoesNotContain(u.Reference, c => char.IsControl(c)); Assert.True(u.Reference.Length <= 120); });
    }
}
