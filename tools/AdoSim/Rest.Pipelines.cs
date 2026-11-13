namespace AdoSim;

public static partial class AdoSimApp
{
    private static void MapPipelines(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/{project}/_apis/pipelines", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var root = Root(ctx);
            var order = (Q(ctx, "orderBy") ?? "name asc").Trim().ToLowerInvariant();
            IEnumerable<SeedPipeline> all = p.Spec.Pipelines ?? [];
            all = order switch
            {
                "id asc" => all.OrderBy(x => x.Id),
                "id desc" => all.OrderByDescending(x => x.Id),
                "name desc" => all.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
                _ => all.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            };
            return Page(ctx, all.ToList(), 500, x => PipelineItem(root, p, x));
        });

        c.MapGet("/{project}/_apis/pipelines/{id:int}", (HttpContext ctx, string project, int id) =>
        {
            var (p, pipeline) = FindPipeline(model, project, id);
            if (pipeline is null) return NotFound();
            var root = Root(ctx);
            var repo = p!.Repos.FirstOrDefault(r => r.Name == pipeline.Repo);
            object configuration = pipeline.Classic is null
                ? new { type = "yaml", path = pipeline.Path, repository = new { id = repo?.Id, type = "azureReposGit" } }
                : new { type = "designerJson" };
            return Results.Json(new
            {
                _links = PipelineLinks(root, p, pipeline), configuration, url = PipelineUrl(root, p, pipeline),
                id, revision = 1, name = pipeline.Name, folder = pipeline.Folder ?? "\\"
            });
        });

        c.MapGet("/{project}/_apis/pipelines/{id:int}/runs", (HttpContext ctx, string project, int id) =>
        {
            var (p, pipeline) = FindPipeline(model, project, id);
            if (pipeline is null) return NotFound();
            var root = Root(ctx);
            var runs = RunsOf(model, pipeline).OrderByDescending(r => r.Started).ThenByDescending(r => r.Run.Id).ToList();
            return Page(ctx, runs, 100, r => RunItem(root, p!, pipeline, r));
        });

        c.MapGet("/{project}/_apis/pipelines/{id:int}/runs/{runId:int}", async (HttpContext ctx, string project, int id, int runId) =>
        {
            var (p, pipeline) = FindPipeline(model, project, id);
            if (pipeline is null) return NotFound();
            var run = RunsOf(model, pipeline).FirstOrDefault(r => r.Run.Id == runId);
            if (run is null) return NotFound();
            var root = Root(ctx);
            var repo = p!.Repos.FirstOrDefault(r => r.Name == pipeline.Repo);
            string? version = null;
            if (repo is not null)
            {
                var rev = run.Run.Version ?? FullRef(run.Run.Branch);
                version = rev.StartsWith('-') ? null : await Git.RevParseAsync(repo, rev + "^{commit}");
            }
            var item = (Dictionary<string, object?>)RunItem(root, p, pipeline, run);
            item["resources"] = new
            {
                repositories = new
                {
                    self = new
                    {
                        repository = new { id = repo?.Id, type = "azureReposGit" },
                        refName = FullRef(run.Run.Branch), version
                    }
                }
            };
            item["variables"] = new Dictionary<string, object>();
            return Results.Json(item);
        });

        c.MapGet("/{project}/_apis/build/definitions", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var root = Root(ctx);
            IEnumerable<SeedPipeline> all = (p.Spec.Pipelines ?? []).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
            if (Q(ctx, "name") is { Length: > 0 } name) all = all.Where(x => WildcardMatch(x.Name, name));
            if (Q(ctx, "path") is { Length: > 0 } path) all = all.Where(x => (x.Folder ?? "\\").StartsWith(path, StringComparison.OrdinalIgnoreCase));
            if (Q(ctx, "definitionIds") is { Length: > 0 } ids)
            {
                var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : -1).ToHashSet();
                all = all.Where(x => wanted.Contains(x.Id));
            }
            var details = Flag(ctx, "includeAllProperties");
            return Page(ctx, all.ToList(), 1000, x => details ? Definition(model, root, p, x) : DefinitionRef(model, root, p, x));
        });

        c.MapGet("/{project}/_apis/build/definitions/{id:int}", (HttpContext ctx, string project, int id) =>
        {
            var (p, pipeline) = FindPipeline(model, project, id);
            return pipeline is null ? NotFound() : Results.Json(Definition(model, Root(ctx), p!, pipeline));
        });
    }

    private sealed record RunView(SeedRun Run, DateTimeOffset Started, DateTimeOffset Finished, string Name);

    private static List<RunView> RunsOf(SimModel model, SeedPipeline pipeline)
    {
        var views = new List<RunView>();
        var perDay = new Dictionary<string, int>();
        foreach (var run in (model.Seed.Runs ?? []).Where(r => r.Pipeline == pipeline.Id).OrderBy(r => r.Id))
        {
            var started = run.Started ?? DefaultDate.AddHours(run.Id);
            var day = started.UtcDateTime.ToString("yyyyMMdd");
            perDay[day] = perDay.GetValueOrDefault(day) + 1;
            views.Add(new RunView(run, started, started.AddMinutes(run.Minutes), run.Name ?? $"{day}.{perDay[day]}"));
        }
        return views;
    }

    private static string PipelineUrl(string root, SimProject p, SeedPipeline x) => $"{root}/{p.Id}/_apis/pipelines/{x.Id}?revision=1";

    private static object PipelineLinks(string root, SimProject p, SeedPipeline x) => new
    {
        self = Link(PipelineUrl(root, p, x)), web = Link($"{root}/{Esc(p.Name)}/_build/definition?definitionId={x.Id}")
    };

    private static object PipelineItem(string root, SimProject p, SeedPipeline x) => new
    {
        _links = PipelineLinks(root, p, x), url = PipelineUrl(root, p, x), id = x.Id, revision = 1, name = x.Name,
        folder = x.Folder ?? "\\"
    };

    // Dictionary so the detail endpoint can append resources.
    private static object RunItem(string root, SimProject p, SeedPipeline pipeline, RunView r) => new Dictionary<string, object?>
    {
        ["_links"] = new
        {
            self = Link($"{root}/{p.Id}/_apis/pipelines/{pipeline.Id}/runs/{r.Run.Id}"),
            web = Link($"{root}/{Esc(p.Name)}/_build/results?buildId={r.Run.Id}"),
            pipeline = Link(PipelineUrl(root, p, pipeline))
        },
        ["pipeline"] = new { url = PipelineUrl(root, p, pipeline), id = pipeline.Id, revision = 1, name = pipeline.Name, folder = pipeline.Folder ?? "\\" },
        ["state"] = "completed", ["result"] = r.Run.Result, ["createdDate"] = Iso(r.Started), ["finishedDate"] = Iso(r.Finished),
        ["url"] = $"{root}/{p.Id}/_apis/pipelines/{pipeline.Id}/runs/{r.Run.Id}", ["id"] = r.Run.Id, ["name"] = r.Name
    };

    private static int StableId(string text) => 1 + text.Aggregate(7, (h, ch) => (h * 31 + ch) % 997);

    private static object Queue(string name) => new
    {
        id = StableId("queue:" + name), name, pool = new { id = StableId("pool:" + name), name, isHosted = false }
    };

    private static object DefinitionRef(SimModel model, string root, SimProject p, SeedPipeline x) => new
    {
        quality = "definition", authoredBy = IdentityRef(model, root, null), queue = Queue(x.Classic?.Queue ?? "Default"),
        id = x.Id, name = x.Name, path = x.Folder ?? "\\", type = "build", queueStatus = "enabled", revision = 1,
        createdDate = Iso(DefaultDate), project = ProjectRef(root, p), uri = $"vstfs:///Build/Definition/{x.Id}",
        url = $"{root}/{p.Id}/_apis/build/Definitions/{x.Id}?revision=1",
        _links = new { self = Link($"{root}/{p.Id}/_apis/build/Definitions/{x.Id}?revision=1"), web = Link($"{root}/{Esc(p.Name)}/_build/definition?definitionId={x.Id}") }
    };

    private static object Definition(SimModel model, string root, SimProject p, SeedPipeline x)
    {
        var repo = p.Repos.FirstOrDefault(r => r.Name == x.Repo);
        var variables = (x.Variables ?? []).ToDictionary(v => v.Name,
            v => (object)(v.Secret ? new { isSecret = true } : new { value = v.Value ?? "sim-value" }));
        var classic = x.Classic;
        object process = classic is null
            ? new { yamlFilename = x.Path?.TrimStart('/'), type = 2 }
            : new
            {
                phases = classic.Phases.Select((phase, index) => new
                {
                    steps = phase.Steps.Select(s => ClassicStep(s)).ToArray(), name = phase.Name, refName = $"Phase_{index + 1}",
                    condition = "succeeded()",
                    target = new { executionOptions = new { type = 0 }, allowScriptsAuthAccessOption = false, type = 1 },
                    jobAuthorizationScope = "projectCollection", jobCancelTimeoutInMinutes = 1
                }).ToArray(),
                target = new { agentSpecification = new { identifier = classic.AgentSpec ?? "windows-2019" } },
                type = 1
            };
        var triggers = new List<object>();
        if (classic is null)
            triggers.Add(new { settingsSourceType = 2, batchChanges = false, maxConcurrentBuildsPerBranch = 1, triggerType = "continuousIntegration" });
        else
        {
            if (classic.CiBranches is { Count: > 0 })
                triggers.Add(new
                {
                    branchFilters = classic.CiBranches.Select(b => "+" + FullRef(b)).ToArray(), pathFilters = Array.Empty<string>(),
                    settingsSourceType = 2, batchChanges = classic.Batch, maxConcurrentBuildsPerBranch = 1, triggerType = "continuousIntegration"
                });
            if (classic.ScheduleHour is { } hour)
                triggers.Add(new
                {
                    schedules = new[]
                    {
                        new
                        {
                            branchFilters = classic.CiBranches?.Take(1).Select(b => "+" + FullRef(b)).ToArray() ?? [],
                            timeZoneId = "UTC", startHours = hour, startMinutes = 0, daysToBuild = 31,
                            scheduleJobId = Ids.Guid($"schedule:{x.Id}"), scheduleOnlyWithChanges = true
                        }
                    },
                    triggerType = "schedule"
                });
        }
        return new
        {
            options = Array.Empty<object>(), triggers, variables,
            // A real server lists VariableGroup objects here, not bare ids.
            variableGroups = (x.VariableGroups ?? []).Select(id =>
            {
                var group = model.Seed.VariableGroups?.FirstOrDefault(v => v.Id == id);
                return (object)new { id, name = group?.Name ?? $"group-{id}", type = group?.Type ?? "Vsts" };
            }).ToArray(),
            buildNumberFormat = classic?.BuildNumberFormat,
            jobAuthorizationScope = "projectCollection", jobTimeoutInMinutes = 60, jobCancelTimeoutInMinutes = 5,
            repository = repo is null ? null : new
            {
                id = repo.Id, type = "TfsGit", name = repo.Name, url = RemoteUrl(root, repo), defaultBranch = repo.DefaultRef,
                clean = "false", checkoutSubmodules = false
            },
            process, quality = "definition", authoredBy = IdentityRef(model, root, null),
            queue = Queue(classic?.Queue ?? "Default"), id = x.Id, name = x.Name, path = x.Folder ?? "\\", type = "build",
            queueStatus = "enabled", revision = 1, createdDate = Iso(DefaultDate), project = ProjectRef(root, p),
            uri = $"vstfs:///Build/Definition/{x.Id}", url = $"{root}/{p.Id}/_apis/build/Definitions/{x.Id}?revision=1"
        };
    }

    private static readonly System.Text.RegularExpressions.Regex SecretName = new(
        "password|secret|token|apikey|privatekey|principalkey|kubeconfig", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Inputs whose name looks like a credential are blanked, exactly like a real server does for secret inputs.
    private static object ClassicStep(SeedClassicStep s)
    {
        var (id, version) = TaskCatalog.Resolve(s.Task);
        var inputs = (s.Inputs ?? []).ToDictionary(i => i.Key, i => SecretName.IsMatch(i.Key) ? "" : i.Value);
        return new
        {
            environment = new Dictionary<string, string>(), enabled = s.Enabled, continueOnError = s.ContinueOnError,
            alwaysRun = false, displayName = s.DisplayName, timeoutInMinutes = 0, retryCountOnTaskFailure = 0,
            condition = s.Condition ?? "succeeded()", task = new { id, versionSpec = version, definitionType = "task" }, inputs
        };
    }
}
