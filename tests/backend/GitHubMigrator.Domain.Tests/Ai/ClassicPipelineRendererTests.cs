using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Domain.Tests.Ai;

// A classic (designer) build definition is JSON. The renderer turns it into Azure-Pipelines-like YAML text without a model, so the converter, the redactor, the task accounting and the gates work on the same kind of text
// they always have. It is a rendering and says so; what it cannot carry over is named in notes; nothing is cut (more than the limits is refused).
public sealed class ClassicPipelineRendererTests
{
    private const string Realistic = """
        {
          "id": 2006, "name": "orion-legacy Build", "type": "build", "buildNumberFormat": "$(date:yyyyMMdd)$(rev:.r)", "jobTimeoutInMinutes": 60, "jobCancelTimeoutInMinutes": 5,
          "queue": { "id": 7, "name": "Windows Agents", "pool": { "id": 7, "name": "Windows Agents", "isHosted": false } },
          "repository": { "id": "r1", "type": "TfsGit", "name": "orion-legacy", "clean": "true", "checkoutSubmodules": true },
          "variables": { "BuildConfiguration": { "value": "Release" }, "DeployPassword": { "isSecret": true }, "BuildPlatform": { "value": "Any CPU" } },
          "variableGroups": [ { "id": 101, "name": "orion-common", "type": "Vsts" } ],
          "triggers": [
            { "triggerType": "continuousIntegration", "branchFilters": ["+refs/heads/master","+refs/heads/maintenance/2019"], "pathFilters": [], "batchChanges": true },
            { "triggerType": "schedule", "schedules": [ { "branchFilters": ["+refs/heads/master"], "timeZoneId": "UTC", "startHours": 2, "startMinutes": 0, "daysToBuild": 31, "scheduleOnlyWithChanges": true } ] } ],
          "options": [],
          "process": { "type": 1, "target": { "agentSpecification": { "identifier": "windows-2019" } }, "phases": [ { "name": "Agent job 1", "refName": "Phase_1", "condition": "succeeded()", "jobCancelTimeoutInMinutes": 1, "target": { "type": 1, "executionOptions": { "type": 0 } },
            "steps": [
              { "enabled": true, "continueOnError": false, "alwaysRun": false, "displayName": "Use NuGet 6.x", "timeoutInMinutes": 0, "condition": "succeeded()", "task": { "id": "2c65196a-54fd-4a02-9be8-d9d1837b7c5d", "versionSpec": "1.*", "definitionType": "task" }, "inputs": { "versionSpec": "6.x", "checkLatest": "true" } },
              { "enabled": true, "displayName": "Package site", "condition": "succeeded()", "task": { "id": "e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "versionSpec": "2.*", "definitionType": "task" }, "inputs": { "targetType": "inline", "script": "Write-Host 'one'\nWrite-Host \"two\"\n", "deployPassword": "hunter2", "azureSubscription": "11111111-2222-3333-4444-555555555555", "emptyOne": "" }, "environment": { "SIGN_TOKEN": "abc", "MODE": "ci" } },
              { "enabled": false, "displayName": "Publish", "condition": "succeededOrFailed()", "continueOnError": true, "alwaysRun": true, "task": { "id": "99999999-0000-0000-0000-000000000001", "versionSpec": "3.*", "definitionType": "task" }, "inputs": {} },
              { "enabled": true, "displayName": "A group", "task": { "id": "88888888-0000-0000-0000-000000000002", "versionSpec": "1.*", "definitionType": "metaTask" }, "inputs": { "x": "y" } }
            ] } ] }
        }
        """;

    private static readonly ClassicRenderContext WithEndpoint = new(new Dictionary<string, string> { ["11111111-2222-3333-4444-555555555555"] = "Orion-Azure" }, new Dictionary<string, string>());

    private static ClassicRendering Render(string json, ClassicRenderContext? context = null)
    {
        using var document = JsonDocument.Parse(json);
        return ClassicPipelineRenderer.Render(document.RootElement, context ?? ClassicRenderContext.Empty) ?? throw new InvalidOperationException("not a designer definition");
    }

    private static bool Designer(string json) { using var document = JsonDocument.Parse(json); return ClassicPipelineRenderer.IsDesigner(document.RootElement); }

    // a one-phase definition with the given steps (JSON array text) and extra top-level members (JSON object members, no braces)
    private static string Definition(string steps = "[]", string extra = "", string phaseExtra = "") =>
        "{" + (extra.Length > 0 ? extra + "," : "") + "\"process\": { \"type\": 1, \"phases\": [ { \"name\": \"Agent job 1\", \"refName\": \"Phase_1\"" + (phaseExtra.Length > 0 ? "," + phaseExtra : "") + ", \"steps\": " + steps + " } ] } }";

    private static string Step(string task = "e213ff0f-5d5c-4791-802d-52ea3e7be1f1", string spec = "2.*", string inputs = "{}", string extra = "", string type = "task") =>
        "{ \"displayName\": \"s\", \"enabled\": true" + (extra.Length > 0 ? "," + extra : "") + ", \"task\": { \"id\": \"" + task + "\", \"versionSpec\": \"" + spec + "\", \"definitionType\": \"" + type + "\" }, \"inputs\": " + inputs + " }";

    private static YamlMappingNode Parse(string text)
    {
        var stream = SafeYaml.Load(text);
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static YamlNode Child(YamlMappingNode map, string key) => map.Children.Single(p => ((YamlScalarNode)p.Key).Value == key).Value;

    // ---- the whole thing ----

    [Fact]
    public void A_realistic_definition_is_rendered_as_text_that_says_what_it_is_and_what_it_leaves_out()
    {
        var rendering = Render(Realistic, WithEndpoint);

        Assert.False(rendering.TooLarge);
        const string expected = """
            # Rendered by GitHubMigrator from a classic (designer) build definition. It is not a file in a repository and it is not meant to be run as it is.
            # What the rendering could not carry over or could not show, then the definition:
            # - Continuous integration trigger, not carried over: branches [+refs/heads/master, +refs/heads/maintenance/2019]; changes are batched
            # - Schedule, not carried over: Mon,Tue,Wed,Thu,Fri at 02:00 (UTC), only when there are changes; branches [+refs/heads/master]
            # - 1 variable(s) are secret: they are listed by name, with no value.
            # - 2 step input(s) are not shown because their name looks like a credential.
            # - 1 step input(s) have no value (empty, or not returned by the service).
            # - 1 step input(s) held a service connection's id and are shown as its name.
            # - 1 step(s) are disabled in the definition (shown with enabled: false).
            # - 1 task group(s) are not expanded (shown by id): 88888888-0000-0000-0000-000000000002.
            # - 1 task(s) could not be named (not a built-in task and not in the organization's catalogue; an extension or a removed task?), shown by id: 99999999-0000-0000-0000-000000000001.
            name: '$(date:yyyyMMdd)$(rev:.r)'
            pool:
              name: 'Windows Agents'
            timeoutInMinutes: 60
            cancelTimeoutInMinutes: 1
            variables:
            - name: BuildConfiguration
              value: 'Release'
            - name: BuildPlatform
              value: 'Any CPU'
            - name: DeployPassword
            - group: 'orion-common'
            steps:
            - checkout: self
              clean: true
              submodules: true
            - task: NuGetToolInstaller@1
              displayName: 'Use NuGet 6.x'
              inputs:
                checkLatest: 'true'
                versionSpec: '6.x'
            - task: PowerShell@2
              displayName: 'Package site'
              env:
                MODE: 'ci'
                SIGN_TOKEN: '<withheld: the name of this input looks like a credential>'
              inputs:
                azureSubscription: 'Orion-Azure'
                deployPassword: '<withheld: the name of this input looks like a credential>'
                script: |
                  Write-Host 'one'
                  Write-Host "two"
                targetType: 'inline'
            - task: 99999999-0000-0000-0000-000000000001@3
              displayName: 'Publish'
              enabled: false
              condition: 'succeededOrFailed()'
              continueOnError: true
              alwaysRun: true
            - task: 88888888-0000-0000-0000-000000000002@1
              displayName: 'A group'
              inputs:
                x: 'y'

            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), rendering.Text);
        Assert.Equal(9, rendering.Notes.Count);
        Assert.DoesNotContain("hunter2", rendering.Text); // the value of a credential-named input is never shown
    }

    [Fact]
    public void The_rendering_is_valid_yaml_and_the_tasks_in_it_are_the_ones_the_converter_must_account_for()
    {
        var text = Render(Realistic, WithEndpoint).Text;

        var root = Parse(text);
        var steps = (YamlSequenceNode)Child(root, "steps");
        Assert.Equal(5, steps.Children.Count); // checkout and four tasks
        Assert.Equal(["88888888-0000-0000-0000-000000000002@1", "99999999-0000-0000-0000-000000000001@3", "NuGetToolInstaller@1", "PowerShell@2"], SourceTaskInventory.From(text).Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(AzureYamlTaskAnalyzer.Analyze(text).ParseFailed);
    }

    [Fact]
    public void The_same_definition_always_renders_to_the_same_text_whatever_the_order_of_its_json_members()
    {
        var first = Render(Realistic, WithEndpoint).Text;
        Assert.Equal(first, Render(Realistic, WithEndpoint).Text);
        var reordered = Realistic.Replace("\"deployPassword\": \"hunter2\", \"azureSubscription\": \"11111111-2222-3333-4444-555555555555\", ", "\"azureSubscription\": \"11111111-2222-3333-4444-555555555555\", \"deployPassword\": \"hunter2\", ");
        Assert.NotEqual(Realistic, reordered);
        Assert.Equal(first, Render(reordered, WithEndpoint).Text);
    }

    // ---- what is a designer definition ----

    [Theory]
    [InlineData("""{ "process": { "type": 1, "phases": [ { "name": "a", "steps": [] } ] } }""", true)]
    [InlineData("""{ "process": { "phases": [ { "name": "a", "steps": [] } ] } }""", true)]
    [InlineData("""{ "type": "build", "process": { "type": 1, "phases": [ {} ] } }""", true)]
    [InlineData("""{ "process": { "type": 2, "yamlFilename": "azure-pipelines.yml" } }""", false)]
    [InlineData("""{ "process": { "type": 2, "phases": [ {} ] } }""", false)]
    [InlineData("""{ "process": { "type": 1, "phases": [] } }""", false)]
    [InlineData("""{ "process": { "type": 1 } }""", false)]
    [InlineData("""{ "process": { "type": 1, "phases": "no" } }""", false)]
    [InlineData("""{ "process": "no" }""", false)]
    [InlineData("""{ }""", false)]
    [InlineData("""{ "type": "xaml", "process": { "type": 1, "phases": [ {} ] } }""", false)]
    [InlineData("[]", false)]
    [InlineData("42", false)]
    public void Only_a_definition_with_designer_phases_is_one(string json, bool designer)
    {
        Assert.Equal(designer, Designer(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(designer, ClassicPipelineRenderer.Render(document.RootElement, ClassicRenderContext.Empty) is not null);
    }

    // ---- naming the tasks ----

    [Fact]
    public void A_task_the_table_does_not_know_is_asked_of_the_organizations_catalogue_and_a_name_from_it_is_used_only_if_it_is_plain()
    {
        var steps = "[" + Step("aaaaaaaa-0000-0000-0000-000000000001", "3.*") + "," + Step("aaaaaaaa-0000-0000-0000-000000000002", "1.*") + "," + Step("aaaaaaaa-0000-0000-0000-000000000003", "*") + "]";
        using var document = JsonDocument.Parse(Definition(steps));
        Assert.Equal(["aaaaaaaa-0000-0000-0000-000000000001", "aaaaaaaa-0000-0000-0000-000000000002", "aaaaaaaa-0000-0000-0000-000000000003"], ClassicPipelineRenderer.TaskIdsToLookUp(document.RootElement));

        var names = new Dictionary<string, string> { ["aaaaaaaa-0000-0000-0000-000000000001"] = "CustomDeploy", ["aaaaaaaa-0000-0000-0000-000000000002"] = "not a plain name!" };
        var text = ClassicPipelineRenderer.Render(document.RootElement, new(new Dictionary<string, string>(), names))!.Text;
        Assert.Contains("- task: CustomDeploy@3", text);
        Assert.Contains("- task: aaaaaaaa-0000-0000-0000-000000000002@1", text); // an unsafe name is not used: the id is shown
        Assert.Contains("- task: aaaaaaaa-0000-0000-0000-000000000003@0", text); // no major in the spec
    }

    [Fact]
    public void Only_ids_the_table_does_not_know_are_looked_up_not_task_groups_not_repeats_and_not_more_than_the_limit()
    {
        var known = Step("e213ff0f-5d5c-4791-802d-52ea3e7be1f1");
        var group = Step("bbbbbbbb-0000-0000-0000-000000000001", type: "metaTask");
        var repeated = string.Join(",", Enumerable.Repeat(Step("cccccccc-0000-0000-0000-000000000001"), 3));
        var many = string.Join(",", Enumerable.Range(1, 30).Select(i => Step($"dddddddd-0000-0000-0000-{i:D12}")));
        using var document = JsonDocument.Parse(Definition($"[{known},{group},{repeated},{many}]"));
        var ids = ClassicPipelineRenderer.TaskIdsToLookUp(document.RootElement);
        Assert.Equal(ClassicPipelineRenderer.MaxLookups, ids.Count);
        Assert.Equal("cccccccc-0000-0000-0000-000000000001", ids[0]);
        Assert.DoesNotContain("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", ids);
        Assert.DoesNotContain("bbbbbbbb-0000-0000-0000-000000000001", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());

        using var yaml = JsonDocument.Parse("""{ "process": { "type": 2 } }""");
        Assert.Empty(ClassicPipelineRenderer.TaskIdsToLookUp(yaml.RootElement));
    }

    [Fact]
    public void A_version_the_table_lists_no_folder_for_keeps_the_name_and_says_so_and_a_task_that_is_not_a_guid_is_not_named()
    {
        var rendering = Render(Definition("[" + Step("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "1.*") + "," + Step("not-a-guid", "2.*") + "]"));
        Assert.Contains("- task: PowerShell@1", rendering.Text);
        Assert.Contains("- task: UnknownTask@2", rendering.Text);
        Assert.Contains(rendering.Notes, n => n.StartsWith("1 task(s) are named from their id with a version the built-in table does not list"));
        Assert.Contains(rendering.Notes, n => n.StartsWith("1 task(s) could not be named"));
    }

    [Fact]
    public void A_step_without_a_task_reference_is_left_out_and_counted()
    {
        var rendering = Render(Definition("[" + Step() + ", { \"displayName\": \"x\" }, { \"task\": \"PowerShell@2\" }]"));
        Assert.Single(SourceTaskInventory.From(rendering.Text));
        Assert.Contains("2 step(s) without a task reference are not shown.", rendering.Notes);
    }

    // ---- steps ----

    [Fact]
    public void A_steps_own_settings_are_carried_and_the_defaults_are_not_repeated()
    {
        var step = Step(extra: "\"refName\": \"build_1\", \"condition\": \"eq(variables['x'], 'y')\", \"continueOnError\": true, \"alwaysRun\": true, \"timeoutInMinutes\": 15, \"retryCountOnTaskFailure\": 2");
        var text = Render(Definition("[" + step + ", " + Step(extra: "\"condition\": \"succeeded()\", \"timeoutInMinutes\": 0, \"retryCountOnTaskFailure\": 0, \"continueOnError\": false, \"alwaysRun\": false, \"refName\": \"bad name\"") + "]")).Text;
        var steps = (YamlSequenceNode)Child(Parse(text), "steps");
        var first = (YamlMappingNode)steps.Children[0];
        Assert.Equal(["task", "displayName", "name", "condition", "continueOnError", "alwaysRun", "timeoutInMinutes", "retryCountOnTaskFailure"], first.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        Assert.Equal("eq(variables['x'], 'y')", ((YamlScalarNode)Child(first, "condition")).Value);
        var second = (YamlMappingNode)steps.Children[1];
        Assert.Equal(["task", "displayName"], second.Children.Keys.Select(k => ((YamlScalarNode)k).Value)); // nothing but what differs from the defaults
    }

    [Fact]
    public void An_input_whose_name_looks_like_a_credential_is_not_shown_unless_it_only_refers_to_a_variable()
    {
        var inputs = """{ "password": "sec-pass", "ApiKey": "sec-key", "myToken": "sec-tok", "kubeConfig": "sec-kube", "privateKey": "sec-priv", "secretText": "sec-text", "principalKey": "sec-prin", "token": "$(GitHubToken)", "Password2": " $( DeployPassword ) ", "targetPath": "$(Build.SourcesDirectory)" }""";
        var rendering = Render(Definition("[" + Step(inputs: inputs) + "]"));
        var map = (YamlMappingNode)Child((YamlMappingNode)((YamlSequenceNode)Child(Parse(rendering.Text), "steps")).Children[0], "inputs");
        string V(string key) => ((YamlScalarNode)Child(map, key)).Value!;
        foreach (var name in new[] { "password", "ApiKey", "myToken", "kubeConfig", "privateKey", "secretText", "principalKey" })
            Assert.StartsWith("<withheld", V(name));
        Assert.Equal("$(GitHubToken)", V("token"));       // a reference is not a secret
        Assert.Equal(" $( DeployPassword ) ", V("Password2"));
        Assert.Equal("$(Build.SourcesDirectory)", V("targetPath"));
        Assert.DoesNotContain("sec-", rendering.Text);
        Assert.Contains("7 step input(s) are not shown because their name looks like a credential.", rendering.Notes);
    }

    [Fact]
    public void An_environment_value_gets_the_same_treatment_as_an_input()
    {
        var text = Render(Definition("[" + Step(extra: "\"environment\": { \"AWS_SECRET_ACCESS_KEY\": \"abc123\", \"MODE\": \"ci\", \"bad name\": \"x\", \"TOKEN_REF\": \"$(T)\" }") + "]")).Text;
        Assert.DoesNotContain("abc123", text);
        var env = (YamlMappingNode)Child((YamlMappingNode)((YamlSequenceNode)Child(Parse(text), "steps")).Children[0], "env");
        Assert.Equal(["AWS_SECRET_ACCESS_KEY", "MODE", "TOKEN_REF"], env.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        Assert.Equal("ci", ((YamlScalarNode)Child(env, "MODE")).Value);
    }

    [Fact]
    public void A_service_connections_id_is_shown_as_its_name_only_when_it_is_the_whole_value_and_whatever_its_spelling()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        var inputs = $$"""{ "azureSubscription": "{{id.ToUpperInvariant()}}", "connectedServiceName": "{{id}}", "other": "prefix-{{id}}", "list": "{{id}},{{id}}", "unknown": "99999999-2222-3333-4444-555555555555" }""";
        var rendering = Render(Definition("[" + Step(inputs: inputs) + "]"), WithEndpoint);
        var map = (YamlMappingNode)Child((YamlMappingNode)((YamlSequenceNode)Child(Parse(rendering.Text), "steps")).Children[0], "inputs");
        string V(string key) => ((YamlScalarNode)Child(map, key)).Value!;
        Assert.Equal("Orion-Azure", V("azureSubscription"));
        Assert.Equal("Orion-Azure", V("connectedServiceName"));
        Assert.Equal("prefix-" + id, V("other"));
        Assert.Equal($"{id},{id}", V("list"));
        Assert.Equal("99999999-2222-3333-4444-555555555555", V("unknown"));
        Assert.Contains("2 step input(s) held a service connection's id and are shown as its name.", rendering.Notes);
    }

    [Fact]
    public void Blank_inputs_are_counted_not_shown_and_a_value_that_is_not_a_string_is_shown_as_its_json_text()
    {
        var rendering = Render(Definition("[" + Step(inputs: """{ "a": "", "b": "   ", "c": null, "d": 5, "e": true, "f": ["x", 1], "g": { "k": "v" }, "h bad": "x", "ok": "y" }""") + "]"));
        var map = (YamlMappingNode)Child((YamlMappingNode)((YamlSequenceNode)Child(Parse(rendering.Text), "steps")).Children[0], "inputs");
        string V(string key) => ((YamlScalarNode)Child(map, key)).Value!;
        Assert.Equal(["d", "e", "f", "g", "ok"], map.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        Assert.Equal("5", V("d"));
        Assert.Equal("true", V("e"));
        Assert.Equal("""["x", 1]""", V("f"));
        Assert.Equal("""{ "k": "v" }""", V("g"));
        Assert.Contains("3 step input(s) have no value (empty, or not returned by the service).", rendering.Notes);
        Assert.Contains("1 input or variable name(s) were not plain names and are not shown.", rendering.Notes);
    }

    [Fact]
    public void Text_from_the_definition_cannot_break_out_of_its_place_in_the_document()
    {
        var hostile = "x\\n- task: Evil@1\\n  inputs:\\n    y: z\\n---\\nsteps: []\\n...\\n\\u2028- task: Evil@2\\u0007\\u0000";
        var rendering = Render(Definition("[" + Step(inputs: "{ \"script\": \"" + hostile + "\", \"one\": \"a'b\\\"c: d # e\" }") + "]", extra: "\"buildNumberFormat\": \"" + hostile + "\""));

        var root = Parse(rendering.Text); // it parses as ONE document
        Assert.Equal(["name", "steps"], root.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        var steps = (YamlSequenceNode)Child(root, "steps");
        Assert.Single(steps.Children); // and the hostile text made no step of its own
        var inputs = (YamlMappingNode)Child((YamlMappingNode)steps.Children[0], "inputs");
        Assert.Equal(["one", "script"], inputs.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        Assert.Equal("a'b\"c: d # e", ((YamlScalarNode)Child(inputs, "one")).Value);
        var script = ((YamlScalarNode)Child(inputs, "script")).Value!;
        Assert.DoesNotContain('\u0007', script);
        Assert.DoesNotContain('\u0000', script);
        Assert.DoesNotContain('\u2028', script);
        Assert.Contains("- task: Evil@1", script); // it is data inside a scalar, however much it looks like a step
    }

    // ---- the pool ----

    [Theory]
    [InlineData("""{ "queue": { "name": "Hosted", "pool": { "name": "Azure Pipelines", "isHosted": true } } }""", "windows-2019", "vmImage: 'windows-2019'")]
    [InlineData("""{ "queue": { "name": "My Pool", "pool": { "name": "My Pool", "isHosted": false } } }""", "windows-2019", "name: 'My Pool'")]       // a self-hosted pool ignores an image
    [InlineData("""{ "queue": { "name": "Unknown" } }""", "ubuntu-22.04", "vmImage: 'ubuntu-22.04'")]                                          // hosted or not is not said: the image is what is known
    [InlineData("""{ "queue": { "name": "Unknown" } }""", null, "name: 'Unknown'")]
    [InlineData("""{ "queue": { "pool": { "name": "Pool Only", "isHosted": false } } }""", null, "name: 'Pool Only'")]
    public void The_pool_is_a_hosted_image_or_a_named_pool(string extra, string? image, string expected)
    {
        var members = extra.Trim()[1..^1].Trim(); // the members of the object, without its own braces
        var json = "{" + members + ",\"process\": { \"type\": 1" + (image is null ? "" : $", \"target\": {{ \"agentSpecification\": {{ \"identifier\": \"{image}\" }} }}") + ", \"phases\": [ { \"name\": \"j\", \"steps\": [] } ] } }";
        var text = Render(json).Text;
        Assert.Contains("pool:\n  " + expected + "\n", text);
    }

    [Fact]
    public void Without_a_queue_or_an_image_there_is_no_pool_and_a_server_phase_is_a_server_pool()
    {
        Assert.DoesNotContain("pool:", Render(Definition()).Text);
        var server = Render(Definition(phaseExtra: "\"target\": { \"type\": 2 }", extra: "\"queue\": { \"name\": \"Q\" }, \"repository\": { \"type\": \"TfsGit\", \"clean\": \"true\" }")).Text;
        Assert.Contains("pool: 'server'", server.Replace("pool: server", "pool: 'server'"));
        Assert.DoesNotContain("'Q'", server);
        Assert.DoesNotContain("checkout", server);
    }

    [Fact]
    public void Demands_come_from_the_phase_or_else_from_the_definition_as_plain_text()
    {
        var fromDefinition = Render(Definition(extra: "\"queue\": { \"name\": \"Q\" }, \"demands\": [ { \"name\": \"npm\" }, { \"name\": \"Agent.OS\", \"value\": \"Windows_NT\" }, { \"value\": \"nameless\" } ]")).Text;
        var demands = (YamlSequenceNode)Child((YamlMappingNode)Child(Parse(fromDefinition), "pool"), "demands");
        Assert.Equal(["npm", "Agent.OS -equals Windows_NT"], demands.Children.Select(d => ((YamlScalarNode)d).Value));

        var fromPhase = Render(Definition(extra: "\"queue\": { \"name\": \"Q\" }, \"demands\": [ { \"name\": \"ignored\" } ]", phaseExtra: "\"target\": { \"type\": 1, \"demands\": [ \"docker\", \"Agent.Version -gtEquals 2.144.0\" ] }")).Text;
        var own = (YamlSequenceNode)Child((YamlMappingNode)Child(Parse(fromPhase), "pool"), "demands");
        Assert.Equal(["docker", "Agent.Version -gtEquals 2.144.0"], own.Children.Select(d => ((YamlScalarNode)d).Value));
        // the labels the product reads from a pool are the plain ones
        Assert.Equal(["docker"], AzureRunnerLabels.From(fromPhase));
    }

    // ---- phases ----

    [Fact]
    public void Several_phases_are_jobs_with_their_conditions_and_dependencies_and_one_plain_phase_is_flat()
    {
        const string json = """
            { "process": { "type": 1, "phases": [
              { "name": "Build it", "refName": "Phase_1", "target": { "type": 1 }, "steps": [] },
              { "name": "Test it", "refName": "Phase 2!", "condition": "and(succeeded(), eq(variables['x'], 'y'))", "dependencies": [ { "scope": "Phase_1", "event": "Completed" } ], "target": { "type": 1 }, "steps": [] },
              { "name": "", "refName": "", "target": { "type": 2 }, "dependencies": [ { "scope": "Phase 2!" }, { "scope": "" } ], "steps": [] } ] } }
            """;
        var text = Render(json).Text;
        var jobs = (YamlSequenceNode)Child(Parse(text), "jobs");
        Assert.Equal(3, jobs.Children.Count);
        string Field(int i, string key) => ((YamlScalarNode)Child((YamlMappingNode)jobs.Children[i], key)).Value!;
        Assert.Equal(["Phase_1", "Phase_2", "Job_3"], Enumerable.Range(0, 3).Select(i => Field(i, "job")));
        Assert.Equal("Test it", Field(1, "displayName"));
        Assert.Equal("and(succeeded(), eq(variables['x'], 'y'))", Field(1, "condition"));
        Assert.Equal(["Phase_1"], ((YamlSequenceNode)Child((YamlMappingNode)jobs.Children[1], "dependsOn")).Children.Select(c => ((YamlScalarNode)c).Value));
        Assert.Equal(["Phase_2"], ((YamlSequenceNode)Child((YamlMappingNode)jobs.Children[2], "dependsOn")).Children.Select(c => ((YamlScalarNode)c).Value));
        Assert.Equal("server", Field(2, "pool"));
        Assert.DoesNotContain("\nsteps:", text); // not flat

        // a job whose condition is the default does not repeat it
        var explicitDefault = Render("""{ "process": { "type": 1, "phases": [ { "name": "a", "condition": "succeeded()", "steps": [] }, { "name": "b", "condition": "succeeded()", "steps": [] } ] } }""").Text;
        var defaultJobs = (YamlSequenceNode)Child(Parse(explicitDefault), "jobs");
        Assert.All(defaultJobs.Children.Cast<YamlMappingNode>(), job => Assert.DoesNotContain("condition", job.Children.Keys.Select(k => ((YamlScalarNode)k).Value)));

        Assert.Contains("\nsteps:", Render(Definition(phaseExtra: "\"condition\": \"succeeded()\"")).Text);
        Assert.Contains("\njobs:", Render(Definition(phaseExtra: "\"condition\": \"always()\"")).Text);
        Assert.Contains("\njobs:", Render(Definition(phaseExtra: "\"dependencies\": [ { \"scope\": \"Phase_0\" } ]")).Text);
    }

    [Fact]
    public void Timeouts_come_from_the_phase_else_the_definition_and_zero_means_none()
    {
        var text = Render(Definition(extra: "\"jobTimeoutInMinutes\": 90, \"jobCancelTimeoutInMinutes\": 5", phaseExtra: "\"jobCancelTimeoutInMinutes\": 2")).Text;
        Assert.Contains("timeoutInMinutes: 90\n", text);
        Assert.Contains("cancelTimeoutInMinutes: 2\n", text);
        var ownTimeout = Render(Definition(extra: "\"jobTimeoutInMinutes\": 90", phaseExtra: "\"jobTimeoutInMinutes\": 30")).Text;
        Assert.Contains("timeoutInMinutes: 30\n", ownTimeout);
        Assert.DoesNotContain("timeoutInMinutes: 90", ownTimeout);
        var none = Render(Definition(extra: "\"jobTimeoutInMinutes\": 0")).Text;
        Assert.DoesNotContain("imeoutInMinutes", none);
    }

    [Fact]
    public void An_execution_option_and_a_phase_target_that_are_not_an_agent_job_are_named_not_hidden()
    {
        var rendering = Render(Definition(phaseExtra: "\"name\": \"Matrix\", \"target\": { \"type\": 3, \"executionOptions\": { \"type\": 1, \"multipliers\": \"a,b\" } }"));
        Assert.Contains(rendering.Notes, n => n.Contains("target type 3"));
        Assert.Contains(rendering.Notes, n => n.Contains("multi-configuration execution option"));
        Assert.Contains(Render(Definition(phaseExtra: "\"target\": { \"type\": 1, \"executionOptions\": { \"type\": 2 } }")).Notes, n => n.Contains("multi-agent execution option"));
    }

    // ---- sources ----

    [Theory]
    [InlineData("""{ "type": "TfsGit", "clean": "true" }""", "- checkout: self\n  clean: true\n")]
    [InlineData("""{ "type": "TfsGit", "checkoutSubmodules": true }""", "- checkout: self\n  submodules: true\n")]
    [InlineData("""{ "type": "Git", "properties": { "fetchDepth": "1", "gitLfsSupport": "true" } }""", "- checkout: self\n  fetchDepth: 1\n  lfs: true\n")]
    [InlineData("""{ "type": "TfsGit", "properties": { "skipSyncSource": "true", "fetchDepth": "1" } }""", "- checkout: none\n")]
    public void The_get_sources_options_that_matter_become_a_checkout_step(string repository, string expected)
    {
        Assert.Contains(expected, Render(Definition(extra: "\"repository\": " + repository)).Text);
    }

    [Theory]
    [InlineData("""{ "type": "TfsGit", "clean": "false", "checkoutSubmodules": false, "properties": { "fetchDepth": "0", "gitLfsSupport": "false" } }""")]
    [InlineData("""{ "type": "TfsGit" }""")]
    [InlineData("""{ "type": "GitHub", "clean": "true" }""")]
    [InlineData("""{ "type": "TfsVersionControl", "clean": "true" }""")]
    public void Without_an_option_worth_carrying_or_for_another_kind_of_repository_there_is_no_checkout_step(string repository)
    {
        Assert.DoesNotContain("checkout", Render(Definition(extra: "\"repository\": " + repository)).Text);
    }

    [Fact]
    public void A_repository_that_is_not_azure_repos_git_is_named_in_the_notes_and_the_checkout_is_only_in_the_first_agent_phase()
    {
        Assert.Contains(Render(Definition(extra: "\"repository\": { \"type\": \"TfsVersionControl\" }")).Notes, n => n.StartsWith("The source repository is of type 'TfsVersionControl', not an Azure Repos Git repository"));
        var two = Render("""
            { "repository": { "type": "TfsGit", "clean": "true" }, "process": { "type": 1, "phases": [ { "name": "a", "steps": [] }, { "name": "b", "steps": [] } ] } }
            """).Text;
        Assert.Equal(1, two.Split("checkout: self").Length - 1);
    }

    // ---- variables ----

    [Fact]
    public void Variables_are_listed_by_name_with_values_only_when_they_are_not_secret_and_groups_by_name()
    {
        var rendering = Render(Definition(extra: """
            "variables": { "b": { "value": "2", "allowOverride": true }, "a": { "value": "1" }, "s": { "isSecret": true, "value": "must-not-show" }, "empty": { "value": "" }, "nul": { "value": null }, "bad name": { "value": "x" } },
            "variableGroups": [ { "id": 1, "name": "g one" }, { "id": 2 }, { "name": "" } ]
            """));
        var variables = (YamlSequenceNode)Child(Parse(rendering.Text), "variables");
        Assert.Equal(["a", "b", "empty", "nul", "s", "g one"], variables.Children.Select(v => { var m = (YamlMappingNode)v; var k = (YamlScalarNode)m.Children.Keys.First(); return k.Value == "group" ? ((YamlScalarNode)m.Children[k]).Value! : ((YamlScalarNode)m.Children[new YamlScalarNode("name")]).Value!; }));
        Assert.DoesNotContain("must-not-show", rendering.Text);
        Assert.Contains("1 variable(s) are secret: they are listed by name, with no value.", rendering.Notes);
        var names = variables.Children.Cast<YamlMappingNode>().Where(m => m.Children.ContainsKey(new YamlScalarNode("name"))).ToDictionary(m => ((YamlScalarNode)m.Children[new YamlScalarNode("name")]).Value!, m => m.Children.ContainsKey(new YamlScalarNode("value")));
        Assert.Equal(new Dictionary<string, bool> { ["a"] = true, ["b"] = true, ["empty"] = false, ["nul"] = false, ["s"] = false }, names);
    }

    // ---- what it cannot carry over ----

    [Fact]
    public void Triggers_are_described_never_turned_into_yaml_triggers()
    {
        const string triggers = """
            "triggers": [
              { "triggerType": "continuousIntegration", "branchFilters": [ "+refs/heads/main", "-refs/heads/old" ], "pathFilters": [ "+/src", "-/docs" ] },
              { "triggerType": "pullRequest", "branchFilters": [ "+refs/heads/main" ] },
              { "triggerType": "schedule", "schedules": [ { "timeZoneId": "W. Europe Standard Time", "startHours": 23, "startMinutes": 30, "daysToBuild": 127, "scheduleOnlyWithChanges": false, "branchFilters": [] }, { "startHours": 0, "daysToBuild": 65 }, { "daysToBuild": 0 } ] },
              { "triggerType": "buildCompletion" }, { "triggerType": "gatedCheckIn" }, { "triggerType": "none" }, { }
            ]
            """;
        var rendering = Render(Definition(extra: triggers));
        Assert.DoesNotContain("\ntrigger:", rendering.Text);
        Assert.DoesNotContain("\npr:", rendering.Text);
        Assert.DoesNotContain("\nschedules:", rendering.Text);
        Assert.Contains("Continuous integration trigger, not carried over: branches [+refs/heads/main, -refs/heads/old]; paths [+/src, -/docs]", rendering.Notes);
        Assert.Contains("Pull request trigger, not carried over: branches [+refs/heads/main]", rendering.Notes);
        Assert.Contains("Schedule, not carried over: every day at 23:30 (W. Europe Standard Time); no branch filter", rendering.Notes);
        Assert.Contains("Schedule, not carried over: Mon,Sun at 00:00 (no time zone given); no branch filter", rendering.Notes);
        Assert.Contains("Schedule, not carried over: no days at 00:00 (no time zone given); no branch filter", rendering.Notes);
        Assert.Contains("A build completion trigger exists (not carried over).", rendering.Notes);
        Assert.Contains("A trigger of type 'gatedCheckIn' exists (not carried over).", rendering.Notes);
        Assert.Contains("A trigger of type 'none' exists (not carried over).", rendering.Notes);
        Assert.Contains("A trigger of type 'unknown' exists (not carried over).", rendering.Notes);
    }

    [Fact]
    public void No_trigger_is_said_and_a_long_list_of_triggers_is_cut_in_the_notes_only()
    {
        Assert.Contains(Render(Definition()).Notes, n => n.StartsWith("No trigger: builds are started by hand."));
        var many = string.Join(",", Enumerable.Repeat("""{ "triggerType": "buildCompletion" }""", 14));
        var rendering = Render(Definition(extra: $"\"triggers\": [{many}]"));
        Assert.Equal(10, rendering.Notes.Count(n => n.StartsWith("A build completion trigger")));
        Assert.Contains("4 more trigger(s) not listed.", rendering.Notes);
    }

    [Fact]
    public void There_are_never_more_notes_than_the_limit_however_many_things_could_not_be_carried_over()
    {
        // ten triggers of five schedules each: fifty notes of schedules alone
        var schedule = "{ \"startHours\": 1, \"daysToBuild\": 1 }";
        var trigger = "{ \"triggerType\": \"schedule\", \"schedules\": [" + string.Join(",", Enumerable.Repeat(schedule, 5)) + "] }";
        var rendering = Render(Definition(extra: "\"triggers\": [" + string.Join(",", Enumerable.Repeat(trigger, 10)) + "]"));
        Assert.Equal(ClassicPipelineRenderer.MaxNotes, rendering.Notes.Count);
        Assert.Equal(ClassicPipelineRenderer.MaxNotes, rendering.Text.Split('\n').Count(l => l.StartsWith("# - ")));
    }

    [Fact]
    public void Enabled_build_options_are_counted()
    {
        var rendering = Render(Definition(extra: """ "options": [ { "enabled": true, "definition": { "id": "a" } }, { "enabled": false }, { "enabled": true } ] """));
        Assert.Contains("2 build option(s) are switched on (for example work item creation on failure, badges): not carried over.", rendering.Notes);
        Assert.DoesNotContain(Render(Definition(extra: """ "options": [ { "enabled": false } ] """)).Notes, n => n.Contains("build option"));
    }

    [Fact]
    public void Notes_are_one_short_line_each_and_there_are_never_more_than_the_limit()
    {
        var trigger = "{ \"triggerType\": \"continuousIntegration\", \"branchFilters\": [ \"+refs/heads/" + new string('b', 400) + "\\nsecond line\" ] }";
        var rendering = Render(Definition(extra: $"\"triggers\": [{trigger}]"));
        Assert.All(rendering.Notes, n => { Assert.DoesNotContain('\n', n); Assert.True(n.Length <= 300); });
        Assert.Contains(rendering.Notes, n => n.Length == 300);

        var headerLines = rendering.Text.Split('\n').Where(l => l.StartsWith("# - ")).Count();
        Assert.Equal(rendering.Notes.Count, headerLines);
    }

    // ---- bounds: nothing is cut ----

    [Fact]
    public void More_than_the_limits_is_refused_not_cut()
    {
        string Phases(int n) => "{ \"process\": { \"type\": 1, \"phases\": [" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{ \"name\": \"p{i}\", \"steps\": [] }}")) + "] } }";
        Assert.False(Render(Phases(ClassicPipelineRenderer.MaxPhases)).TooLarge);
        Assert.True(Render(Phases(ClassicPipelineRenderer.MaxPhases + 1)).TooLarge);

        string Steps(int n) => Definition("[" + string.Join(",", Enumerable.Repeat(Step(), n)) + "]");
        Assert.False(Render(Steps(ClassicPipelineRenderer.MaxStepsPerPhase)).TooLarge);
        var tooMany = Render(Steps(ClassicPipelineRenderer.MaxStepsPerPhase + 1));
        Assert.True(tooMany.TooLarge);
        Assert.Equal("", tooMany.Text);

        string Inputs(int n) => Definition("[" + Step(inputs: "{" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"i{i:D3}\": \"v\"")) + "}") + "]");
        Assert.False(Render(Inputs(ClassicPipelineRenderer.MaxInputsPerStep)).TooLarge);
        Assert.True(Render(Inputs(ClassicPipelineRenderer.MaxInputsPerStep + 1)).TooLarge);

        string Variables(int n) => Definition(extra: "\"variables\": {" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"v{i:D4}\": {{ \"value\": \"x\" }}")) + "}");
        Assert.False(Render(Variables(ClassicPipelineRenderer.MaxVariables)).TooLarge);
        Assert.True(Render(Variables(ClassicPipelineRenderer.MaxVariables + 1)).TooLarge);
    }

    [Fact]
    public void A_value_longer_than_the_limit_and_a_rendering_longer_than_the_limit_are_refused()
    {
        Assert.False(Render(Definition("[" + Step(inputs: $"{{ \"script\": \"{new string('x', ClassicPipelineRenderer.MaxValueChars)}\" }}") + "]")).TooLarge);
        Assert.True(Render(Definition("[" + Step(inputs: $"{{ \"script\": \"{new string('x', ClassicPipelineRenderer.MaxValueChars + 1)}\" }}") + "]")).TooLarge);

        // each value is within its limit, the whole is not
        var big = string.Join(",", Enumerable.Range(0, 6).Select(i => Step(inputs: $"{{ \"script\": \"{new string((char)('a' + i), 40_000)}\" }}")));
        var rendering = Render(Definition("[" + big + "]"));
        Assert.True(rendering.TooLarge);
        Assert.Equal("", rendering.Text);
        Assert.NotEmpty(rendering.Notes);
    }

    [Fact]
    public void The_text_that_is_not_refused_is_never_longer_than_what_the_converter_reads()
    {
        var text = Render(Definition("[" + string.Join(",", Enumerable.Range(0, 5).Select(i => Step(inputs: $"{{ \"script\": \"{new string('z', 30_000)}\" }}"))) + "]")).Text;
        Assert.True(text.Length <= ClassicPipelineRenderer.MaxChars);
        Assert.True(Encoding.UTF8.GetByteCount(text) > 100_000);
    }
}
