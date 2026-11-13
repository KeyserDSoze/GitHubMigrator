using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AdoSim.Tests;

// Real YAML ships with the seed: it must parse, look like Azure Pipelines, and every reference must resolve.
public sealed class SeedYamlTests
{
    private static readonly SeedSpec Orion = SeedSpec.Load("orion");

    public sealed record YamlFile(SeedProject Project, SeedRepo Repo, string Path, string Text, string? Branch)
    {
        public override string ToString() => $"{Project.Name}/{Repo.Name}:{Branch ?? "default"}:{Path}";
    }

    public static IEnumerable<YamlFile> AllYaml(SeedSpec seed)
    {
        foreach (var project in seed.Projects)
            foreach (var repo in project.Repos ?? [])
            {
                foreach (var (path, text) in DefaultBranchFiles(repo))
                    if (IsYaml(path)) yield return new YamlFile(project, repo, path, text, null);
                var branchFolders = repo.BranchFilesFrom ?? new Dictionary<string, string>();
                foreach (var (branch, folder) in branchFolders)
                    foreach (var (path, content) in SeedFiles.Read(folder))
                        if (IsYaml(path)) yield return new YamlFile(project, repo, path, Encoding.UTF8.GetString(content), branch);
                foreach (var (branch, files) in repo.BranchFiles ?? new Dictionary<string, Dictionary<string, string>>())
                    foreach (var (path, text) in files)
                        if (IsYaml(path)) yield return new YamlFile(project, repo, path, text, branch);
            }
    }

    private static bool IsYaml(string path) => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, string> DefaultBranchFiles(SeedRepo repo)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (repo.FilesFrom is not null)
            foreach (var (path, content) in SeedFiles.Read(repo.FilesFrom)) files[path] = Encoding.UTF8.GetString(content);
        foreach (var (path, text) in repo.Files ?? []) files[path.TrimStart('/')] = text;
        return files;
    }

    public static YamlMappingNode Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static IEnumerable<(string Key, YamlNode Value, YamlMappingNode Parent)> Pairs(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (k, v) in map.Children)
                {
                    if (k is YamlScalarNode key && key.Value is not null) yield return (key.Value, v, map);
                    foreach (var inner in Pairs(v)) yield return inner;
                }
                break;
            case YamlSequenceNode list:
                foreach (var child in list.Children)
                    foreach (var inner in Pairs(child)) yield return inner;
                break;
        }
    }

    public static IEnumerable<object[]> OrionFiles() => AllYaml(Orion).Select(f => new object[] { f.ToString() });

    private static YamlFile Find(string label) => AllYaml(Orion).Single(f => f.ToString() == label);

    [Fact]
    public void The_estate_ships_real_yaml_in_every_flavour()
    {
        var files = AllYaml(Orion).ToList();
        Assert.True(files.Count >= 15, $"only {files.Count} yaml files");
        Assert.Contains(files, f => f.Branch is not null);
        Assert.Contains(files, f => f.Path.StartsWith(".azuredevops", StringComparison.Ordinal) || f.Path.Contains("/templates/"));
        Assert.DoesNotContain(files, f => f.Repo.Name == "orion-legacy");
        Assert.All(files, f => Assert.DoesNotContain('\r', f.Text));
    }

    [Fact]
    public void Every_embedded_seed_folder_belongs_to_a_repository_of_the_seed()
    {
        var referenced = Orion.Projects.SelectMany(p => p.Repos ?? [])
            .SelectMany(r => new[] { r.FilesFrom }.Concat(r.BranchFilesFrom?.Values ?? Enumerable.Empty<string>())).OfType<string>().ToHashSet();
        var embedded = typeof(SeedSpec).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("seed/files/", StringComparison.Ordinal))
            .Select(n => n.Replace('\\', '/')["seed/files/".Length..].Split('/')[0]).ToHashSet();
        Assert.True(embedded.Count >= 10, "seed files were not embedded");
        Assert.Equal(embedded.Order(), referenced.Order());
    }

    [Theory]
    [MemberData(nameof(OrionFiles))]
    public void Yaml_parses_and_has_the_shape_of_azure_pipelines(string label)
    {
        var file = Find(label);
        var root = Parse(file.Text);
        var keys = root.Children.Keys.Select(Scalar).ToHashSet();
        var allowed = new HashSet<string?>
        {
            "name", "trigger", "pr", "schedules", "resources", "parameters", "variables", "pool", "stages", "jobs", "steps",
            "extends", "container", "services", "strategy", "appendCommitMessageToRunName"
        };
        Assert.Empty(keys.Except(allowed));
        if (!(keys.Count == 1 && keys.Contains("variables")))
            Assert.Equal(1, keys.Count(k => k is "stages" or "jobs" or "steps"));

        var stepKeys = new HashSet<string> { "script", "bash", "pwsh", "powershell", "task", "checkout", "download", "downloadBuild", "template", "publish", "getPackage", "reviewApp" };
        foreach (var (key, value, parent) in Pairs(root))
        {
            if (key == "steps" && value is YamlSequenceNode steps)
                foreach (var step in steps.Children)
                {
                    var map = Assert.IsType<YamlMappingNode>(step);
                    var stepProperties = map.Children.Keys.Select(Scalar).ToList();
                    Assert.True(stepProperties.Any(k => k is not null && (stepKeys.Contains(k) || k.StartsWith("${{", StringComparison.Ordinal))),
                        $"{file}: step without a known type: {string.Join(",", stepProperties)}");
                }
            if (key == "task") Assert.Matches(@"^[A-Za-z0-9]+@\d+(\.\d+)*$", Scalar(value)!);
            if (key == "deployment")
            {
                Assert.True(parent.Children.ContainsKey("environment"), $"{file}: deployment {Scalar(value)} has no environment");
                Assert.True(parent.Children.ContainsKey("strategy"), $"{file}: deployment {Scalar(value)} has no strategy");
            }
            if (key == "pool" && value is YamlMappingNode pool)
                Assert.True(pool.Children.ContainsKey("vmImage") || pool.Children.ContainsKey("name"), $"{file}: pool without vmImage or name");
            if (key == "trigger")
                Assert.True(value is YamlSequenceNode or YamlMappingNode || Scalar(value) == "none", $"{file}: unusual trigger");
        }
    }

    [Fact]
    public void Default_seed_inline_yaml_is_valid_too()
    {
        var files = AllYaml(SeedSpec.Load("default")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files) Parse(file.Text);
    }

    private static readonly string[] ConnectionKeys =
    [
        "azureSubscription", "connectedServiceName", "connectedServiceNameARM", "containerRegistry", "dockerRegistryServiceConnection",
        "externalFeedCredentials", "publishFeedCredentials", "customEndpoint", "publishEndpoint", "sshEndpoint",
        "kubernetesServiceEndpoint", "kubernetesServiceConnection", "SonarQube", "gitHubConnection", "endpoint",
        "serviceConnection", "registryConnection"
    ];

    private static SeedServiceEndpoint? Endpoint(string name, SeedProject project) =>
        Orion.ServiceEndpoints!.FirstOrDefault(e => e.Name == name && SimModel.Visible(e.Projects, project.Name));

    private static (SeedProject Project, SeedRepo Repo)? Repo(string reference, SeedProject current)
    {
        var parts = reference.Split('/', 2);
        var project = parts.Length == 2 ? Orion.Projects.FirstOrDefault(p => p.Name == parts[0]) : current;
        var repo = project?.Repos?.FirstOrDefault(r => r.Name == parts[^1]);
        return repo is null ? null : (project!, repo);
    }

    private static bool RefExists(SeedRepo repo, string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..] == repo.DefaultBranch || (repo.Branches ?? []).Contains(reference["refs/heads/".Length..])
            : reference.StartsWith("refs/tags/", StringComparison.Ordinal) &&
              ((repo.Tags ?? []).Contains(reference["refs/tags/".Length..]) || (repo.AnnotatedTags ?? []).Contains(reference["refs/tags/".Length..]));

    private static string Normalize(string path)
    {
        var stack = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }

    [Fact]
    public void Every_reference_resolves_inside_the_seed()
    {
        var errors = new List<string>();
        void Fail(YamlFile f, string message) => errors.Add($"{f}: {message}");
        static bool Dynamic(string value) => value.Contains("$(") || value.Contains("${{") || value.Length == 0;

        foreach (var file in AllYaml(Orion))
        {
            var root = Parse(file.Text);
            var aliases = new Dictionary<string, (SeedProject Project, SeedRepo Repo)?>();
            var checkoutAliases = new HashSet<string> { "self", "none", "templates" };

            if (root.Children.TryGetValue("resources", out var resources) && resources is YamlMappingNode resourceMap)
            {
                if (resourceMap.Children.TryGetValue("repositories", out var repositories) && repositories is YamlSequenceNode repoList)
                    foreach (var item in repoList.Children.OfType<YamlMappingNode>())
                    {
                        var alias = Scalar(item.Children[new YamlScalarNode("repository")])!;
                        var type = item.Children.TryGetValue(new YamlScalarNode("type"), out var t) ? Scalar(t)! : "git";
                        var name = Scalar(item.Children[new YamlScalarNode("name")])!;
                        var reference = item.Children.TryGetValue(new YamlScalarNode("ref"), out var r) ? Scalar(r) : null;
                        checkoutAliases.Add(alias);
                        if (type == "git")
                        {
                            var target = Repo(name, file.Project);
                            aliases[alias] = target;
                            if (target is null) { Fail(file, $"repository resource '{alias}' points at unknown repo '{name}'"); continue; }
                            if (reference is not null && !RefExists(target.Value.Repo, reference))
                                Fail(file, $"repository resource '{alias}' uses missing ref '{reference}'");
                        }
                        else if (type == "github")
                        {
                            var endpoint = item.Children.TryGetValue(new YamlScalarNode("endpoint"), out var e) ? Scalar(e)! : "";
                            if (Endpoint(endpoint, file.Project)?.Type != "github") Fail(file, $"github resource '{alias}' needs a github service connection, got '{endpoint}'");
                        }
                    }
                if (resourceMap.Children.TryGetValue("containers", out var containers) && containers is YamlSequenceNode containerList)
                    foreach (var item in containerList.Children.OfType<YamlMappingNode>())
                        if (item.Children.TryGetValue(new YamlScalarNode("endpoint"), out var endpoint) && Endpoint(Scalar(endpoint)!, file.Project)?.Type != "dockerregistry")
                            Fail(file, $"container resource endpoint '{Scalar(endpoint)}' is not a dockerregistry connection");
                if (resourceMap.Children.TryGetValue("pipelines", out var pipelines) && pipelines is YamlSequenceNode pipelineList)
                    foreach (var item in pipelineList.Children.OfType<YamlMappingNode>())
                    {
                        var source = Scalar(item.Children[new YamlScalarNode("source")])!;
                        var projectName = item.Children.TryGetValue(new YamlScalarNode("project"), out var p) ? Scalar(p)! : file.Project.Name;
                        var found = Orion.Projects.FirstOrDefault(x => x.Name == projectName)?.Pipelines?.Any(x => x.Name == source) == true;
                        if (!found) Fail(file, $"pipeline resource source '{source}' does not exist in '{projectName}'");
                    }
            }

            foreach (var (key, value, _) in Pairs(root))
            {
                var text = Scalar(value);
                if (key == "template" && text is not null && !text.Contains("${{"))
                {
                    var parts = text.Split('@');
                    var alias = parts.Length > 1 ? parts[1] : null;
                    (SeedProject Project, SeedRepo Repo)? target = alias is null or "self" ? (file.Project, file.Repo) : aliases.GetValueOrDefault(alias);
                    if (alias is not null && alias != "self" && !aliases.ContainsKey(alias)) { Fail(file, $"template '{text}' uses undeclared repository alias '{alias}'"); continue; }
                    if (target is null) continue;
                    var path = alias is null
                        ? Normalize(System.IO.Path.GetDirectoryName(file.Path)!.Replace('\\', '/') + "/" + parts[0])
                        : Normalize(parts[0]);
                    if (parts[0].StartsWith('/')) path = Normalize(parts[0]);
                    if (!DefaultBranchFiles(target.Value.Repo).ContainsKey(path))
                        Fail(file, $"template '{text}' resolves to '{target.Value.Repo.Name}:{path}' which does not exist");
                }
                if (key == "checkout" && text is not null && !checkoutAliases.Contains(text)) Fail(file, $"checkout '{text}' is not declared");
                if (key == "group" && text is not null && !Dynamic(text))
                {
                    var group = Orion.VariableGroups!.FirstOrDefault(g => g.Name == text && SimModel.Visible(g.Projects, file.Project.Name));
                    if (group is null) Fail(file, $"variable group '{text}' is missing or not visible in {file.Project.Name}");
                }
                if (key == "environment")
                {
                    var name = text ?? Scalar(((YamlMappingNode)value).Children[new YamlScalarNode("name")]);
                    if (name is not null && !Dynamic(name) && !Orion.Environments!.Any(e => e.Name == name.Split('.')[0] && e.Project == file.Project.Name))
                        Fail(file, $"environment '{name}' does not exist in {file.Project.Name}");
                }
                if (ConnectionKeys.Contains(key) && text is not null && !Dynamic(text) && Endpoint(text, file.Project) is null)
                    Fail(file, $"{key} '{text}' is not a service connection visible in {file.Project.Name}");
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void Registered_pipelines_point_at_existing_files_and_declare_the_groups_their_yaml_uses()
    {
        var errors = new List<string>();
        foreach (var project in Orion.Projects)
            foreach (var pipeline in project.Pipelines ?? [])
            {
                var repo = project.Repos!.Single(r => r.Name == pipeline.Repo);
                if (pipeline.Classic is not null)
                {
                    Assert.Null(pipeline.Path);
                    Assert.DoesNotContain(DefaultBranchFiles(repo).Keys, p => IsYaml(p));
                    Assert.NotEmpty(pipeline.Classic.Phases.SelectMany(p => p.Steps));
                    continue;
                }
                var files = DefaultBranchFiles(repo);
                if (!files.TryGetValue(pipeline.Path!.TrimStart('/'), out var text)) { errors.Add($"{pipeline.Name}: {pipeline.Path} missing in {repo.Name}"); continue; }
                var groupNames = Pairs(Parse(text)).Where(p => p.Key == "group").Select(p => Scalar(p.Value)!).ToList();
                var declared = (pipeline.VariableGroups ?? []).Select(id => Orion.VariableGroups!.Single(g => g.Id == id).Name).ToHashSet();
                foreach (var group in groupNames.Where(g => !declared.Contains(g)))
                    errors.Add($"{pipeline.Name}: yaml uses group '{group}' that the definition does not list");
                foreach (var id in pipeline.VariableGroups ?? [])
                    if (!SimModel.Visible(Orion.VariableGroups!.Single(g => g.Id == id).Projects, project.Name)) errors.Add($"{pipeline.Name}: group {id} is not visible in {project.Name}");
            }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void The_estate_contains_the_pipeline_constructs_a_converter_has_to_handle()
    {
        string Text(string label) => Find(label).Text;
        var api = Text("Orion Commerce/orion-api:default:azure-pipelines.yml");
        foreach (var token in new[] { "UseDotNet@2", "DotNetCoreCLI@2", "PublishTestResults@2", "PublishCodeCoverageResults@2", "PublishPipelineArtifact@1",
                     "Docker@2", "command: buildAndPush", "AzureWebApp@1", "AzureCLI@2", "strategy:", "runOnce:", "environment: dev", "template: steps/security-scan.yml@templates",
                     "template: pipelines/templates/dotnet-quality.yml", "- group:", "- template: pipelines/templates/variables.yml", "parameters:",
                     "containerRegistry: orion-acr", "and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))", "stage: Deploy_Prod", "pr:", "exclude:" })
            Assert.Contains(token, api);
        var web = Text("Orion Commerce/orion-web:default:azure-pipelines.yml");
        foreach (var token in new[] { "matrix:", "node_22:", "NodeTool@0", "Npm@1", "Cache@2", "bash:", "pwsh:", "workingDirectory:", "env:", "schedules:", "cron:", "vmImage: ubuntu-latest", "name: SelfHostedLinux", "demands:", "PublishBuildArtifacts@1" })
            Assert.Contains(token, web);
        var lib = Text("Orion Commerce/orion-lib:default:azure-pipelines.yml");
        foreach (var token in new[] { "NuGetToolInstaller@1", "NuGetCommand@2", "command: pack", "$(Build.BuildId)", "$(Build.SourceBranchName)", "$(Build.BuildNumber)", "name: 1.4" })
            Assert.Contains(token, lib);
        var infra = Text("Orion Platform/orion-infra:default:pipelines/terraform.yml") + Text("Orion Platform/orion-infra:default:pipelines/bicep.yml");
        foreach (var token in new[] { "TerraformInstaller@1", "inlineScript:", "checkout: self", "checkout: templates", "${{ if eq(parameters.env, 'prod') }}:", "${{ each environment in parameters.environments }}:", "values:", "dependsOn:", "environment: infra-prod" })
            Assert.Contains(token, infra);
        var windows = Text("Orion Commerce/orion-windows:default:build/windows-ci.yml");
        foreach (var token in new[] { "windows-latest", "PowerShell@2", "VSBuild@1", "VSTest@2", "container: dotnet-sdk", "services:", "pipelines:", "trigger:" })
            Assert.Contains(token, windows);
        Assert.Contains("stages/deploy-environment.yml@templates", Text("Orion Commerce/orion-api:feature/loyalty/points:azure-pipelines-experimental.yml"));
    }
}
