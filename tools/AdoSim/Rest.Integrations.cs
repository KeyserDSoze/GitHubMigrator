namespace AdoSim;

public static partial class AdoSimApp
{
    private static void MapIntegrations(RouteGroupBuilder c, SimModel model)
    {
        MapServiceConnections(c, model);
        MapVariableGroups(c, model);
        MapEnvironments(c, model);
        MapPolicies(c, model);
        MapHooks(c, model);
    }

    // ---- service connections -------------------------------------------------------------------

    private static string ServiceEndpointId(SeedServiceEndpoint e) => e.Id ?? Ids.Guid("endpoint:" + e.Name);

    private static string DefaultScheme(string type) => type.ToLowerInvariant() switch
    {
        "azurerm" => "ServicePrincipal",
        "github" => "PersonalAccessToken",
        "dockerregistry" or "ssh" or "generic" => "UsernamePassword",
        "kubernetes" => "Kubeconfig",
        "sonarqube" or "npm" or "externalnpmregistry" or "externalnugetfeed" => "Token",
        _ => "None"
    };

    // Names of the secret parameters a server lists with a null value.
    private static string[] SecretKeys(string type, string scheme) => (type.ToLowerInvariant(), scheme) switch
    {
        ("azurerm", "ServicePrincipal") => ["serviceprincipalkey"],
        ("github", "PersonalAccessToken") => ["accessToken"],
        (_, "Token") => ["apitoken"],
        (_, "UsernamePassword") => ["password"],
        (_, "Kubeconfig") => ["kubeconfig"],
        _ => []
    };

    private static object ServiceEndpoint(SimModel model, string root, SeedServiceEndpoint e)
    {
        var scheme = e.Scheme ?? DefaultScheme(e.Type);
        var parameters = new Dictionary<string, string?>();
        foreach (var (key, value) in e.Parameters ?? []) parameters[key] = SecretName.IsMatch(key) ? null : value;
        foreach (var key in SecretKeys(e.Type, scheme)) parameters[key] = null;
        var id = ServiceEndpointId(e);
        return new
        {
            data = e.Data ?? new Dictionary<string, string>(), id, name = e.Name, type = e.Type, url = e.Url ?? "",
            createdBy = IdentityRef(model, root, e.CreatedBy), description = e.Description ?? "",
            authorization = new { parameters, scheme }, isShared = e.IsShared, isReady = e.IsReady, owner = "library",
            serviceEndpointProjectReferences = model.Projects.Where(p => SimModel.Visible(e.Projects, p.Name))
                .Select(p => new { projectReference = new { id = p.Id, name = p.Name }, name = e.Name, description = e.Description ?? "" }).ToArray()
        };
    }

    private static void MapServiceConnections(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/{project}/_apis/serviceendpoint/endpoints", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            IEnumerable<SeedServiceEndpoint> all = (model.Seed.ServiceEndpoints ?? [])
                .Where(e => p is null || SimModel.Visible(e.Projects, p.Name));
            if (Q(ctx, "type") is { Length: > 0 } type) all = all.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase));
            if (Q(ctx, "endpointNames") is { Length: > 0 } names)
            {
                var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                all = all.Where(e => wanted.Contains(e.Name));
            }
            if (Q(ctx, "endpointIds") is { Length: > 0 } ids)
            {
                var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                all = all.Where(e => wanted.Contains(ServiceEndpointId(e)));
            }
            if (Q(ctx, "authSchemes") is { Length: > 0 } schemes)
            {
                var wanted = schemes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                all = all.Where(e => wanted.Contains(e.Scheme ?? DefaultScheme(e.Type)));
            }
            if (Q(ctx, "owner") is { Length: > 0 } owner && !owner.Equals("library", StringComparison.OrdinalIgnoreCase)) all = [];
            var root = Root(ctx);
            var value = all.Select(e => ServiceEndpoint(model, root, e)).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/{project}/_apis/serviceendpoint/endpoints/{id}", (HttpContext ctx, string project, string id) =>
        {
            var p = Find(model, project);
            var e = (model.Seed.ServiceEndpoints ?? []).FirstOrDefault(x =>
                ServiceEndpointId(x).Equals(id, StringComparison.OrdinalIgnoreCase) && (p is null || SimModel.Visible(x.Projects, p.Name)));
            return e is null ? NotFound() : Results.Json(ServiceEndpoint(model, Root(ctx), e));
        });
    }

    // ---- variable groups -------------------------------------------------------------------------

    private static object VariableGroup(SimModel model, string root, SeedVariableGroup g)
    {
        var keyVault = g.KeyVault is not null;
        var variables = g.Variables.ToDictionary(v => v.Name, v => keyVault
            ? (object)new { enabled = true, contentType = "", isSecret = true }
            : v.Secret ? new { isSecret = true } : new { value = v.Value ?? "sim-value" });
        var endpoint = model.Seed.ServiceEndpoints?.FirstOrDefault(e => e.Name == g.KeyVaultEndpoint);
        var projects = model.Projects.Where(p => SimModel.Visible(g.Projects, p.Name)).ToList();
        return new
        {
            variables, id = g.Id, type = keyVault ? "AzureKeyVault" : g.Type ?? "Vsts", name = g.Name, description = g.Description ?? "",
            createdBy = IdentityRef(model, root, g.CreatedBy), createdOn = Iso(DefaultDate),
            modifiedBy = IdentityRef(model, root, g.CreatedBy), modifiedOn = Iso(DefaultDate), isShared = projects.Count > 1,
            variableGroupProjectReferences = projects.Select(p => new
            { name = g.Name, description = g.Description ?? "", projectReference = new { id = p.Id, name = p.Name } }).ToArray(),
            providerData = keyVault ? new { serviceEndpointId = endpoint is null ? null : ServiceEndpointId(endpoint), vault = g.KeyVault } : null
        };
    }

    private static void MapVariableGroups(RouteGroupBuilder c, SimModel model)
    {
        IEnumerable<SeedVariableGroup> Visible(string project)
        {
            var p = Find(model, project);
            return (model.Seed.VariableGroups ?? []).Where(g => p is null || SimModel.Visible(g.Projects, p.Name));
        }

        c.MapGet("/{project}/_apis/distributedtask/variablegroups", (HttpContext ctx, string project) =>
        {
            var all = Visible(project);
            if (Q(ctx, "groupName") is { Length: > 0 } name) all = all.Where(g => WildcardMatch(g.Name, name));
            if (Q(ctx, "ids") is { Length: > 0 } ids)
            {
                var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : -1).ToHashSet();
                all = all.Where(g => wanted.Contains(g.Id));
            }
            // Real server: newest first by default; the continuation token is the last id of the previous page.
            var ascending = string.Equals(Q(ctx, "queryOrder"), "IdAscending", StringComparison.OrdinalIgnoreCase);
            var ordered = (ascending ? all.OrderBy(g => g.Id) : all.OrderByDescending(g => g.Id)).AsEnumerable();
            if (Int(ctx, "continuationToken") is { } token) ordered = ordered.Where(g => ascending ? g.Id > token : g.Id < token);
            var top = Math.Max(Int(ctx, "$top") ?? 50, 0);
            var page = ordered.Take(top + 1).ToList();
            if (page.Count > top)
            {
                page.RemoveAt(top);
                ctx.Response.Headers["X-MS-ContinuationToken"] = page[^1].Id.ToString();
            }
            var root = Root(ctx);
            var value = page.Select(g => VariableGroup(model, root, g)).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/{project}/_apis/distributedtask/variablegroups/{id:int}", (HttpContext ctx, string project, int id) =>
        {
            var g = Visible(project).FirstOrDefault(x => x.Id == id);
            return g is null ? NotFound() : Results.Json(VariableGroup(model, Root(ctx), g));
        });
    }

    // ---- environments and checks -----------------------------------------------------------------

    private const string ApprovalCheckType = "8c6f20a7-a545-4486-9777-f762fafe0d4d";
    private const string TaskCheckType = "fe1de3ee-a436-41b4-bb20-f6eb4cb879a7";

    private static object EnvironmentItem(SimModel model, string root, SeedEnvironment e) => new
    {
        createdBy = IdentityRef(model, root, null), createdOn = Iso(DefaultDate),
        lastModifiedBy = IdentityRef(model, root, null), lastModifiedOn = Iso(DefaultDate),
        id = e.Id, name = e.Name, description = e.Description ?? ""
    };

    // Like the real API, settings only come back with $expand=settings.
    private static object CheckConfiguration(SimModel model, string root, SimProject p, SeedEnvironment e, SeedCheck check, int index, bool expandSettings)
    {
        var id = e.Id * 100 + index + 1;
        object settings;
        object type;
        if (check.Type.Equals("approval", StringComparison.OrdinalIgnoreCase))
        {
            type = new { id = ApprovalCheckType, name = "Approval" };
            settings = new
            {
                approvers = (check.Approvers ?? []).Select(a => new { displayName = model.FindByName(a)?.Display ?? a, id = model.FindByName(a)?.Id ?? Ids.Guid("identity:" + a) }).ToArray(),
                executionOrder = check.Order ?? "anyOrder", minRequiredApprovers = check.MinApprovers ?? 1,
                instructions = check.Instructions ?? "", blockedApprovers = Array.Empty<object>()
            };
        }
        else
        {
            type = new { id = TaskCheckType, name = "Task Check" };
            settings = new
            {
                definitionRef = new { id = "86b05a0c-73e0-4a1e-b4de-1b4da7b6a4a0", name = "evaluatebranchProtection", version = "0.0.1" },
                displayName = "Branch control",
                inputs = new Dictionary<string, string>
                {
                    ["allowedBranches"] = check.Branches ?? "refs/heads/main", ["ensureProtectionOfBranch"] = "false",
                    ["allowUnknownStatusBranch"] = "false"
                },
                retryInterval = 5
            };
        }
        var url = $"{root}/{p.Id}/_apis/pipelines/checks/configurations/{id}";
        var json = new Dictionary<string, object?>
        {
            ["id"] = id, ["timeout"] = check.Timeout, ["type"] = type, ["createdBy"] = IdentityRef(model, root, null), ["createdOn"] = Iso(DefaultDate),
            ["modifiedBy"] = IdentityRef(model, root, null), ["modifiedOn"] = Iso(DefaultDate),
            ["resource"] = new { type = "environment", id = e.Id.ToString(), name = e.Name }, ["version"] = 1,
            ["_links"] = new { self = Link(url), resource = Link($"{root}/{p.Id}/_apis/distributedtask/environments/{e.Id}") }, ["url"] = url
        };
        if (expandSettings) json["settings"] = settings;
        return json;
    }

    private static void MapEnvironments(RouteGroupBuilder c, SimModel model)
    {
        IEnumerable<SeedEnvironment> InProject(SimProject p) => (model.Seed.Environments ?? []).Where(e => e.Project == p.Name);

        c.MapGet("/{project}/_apis/distributedtask/environments", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var all = InProject(p);
            if (Q(ctx, "name") is { Length: > 0 } name) all = all.Where(e => WildcardMatch(e.Name, name));
            var root = Root(ctx);
            return Page(ctx, all.OrderBy(e => e.Id).ToList(), 50, e => EnvironmentItem(model, root, e));
        });

        c.MapGet("/{project}/_apis/distributedtask/environments/{id:int}", (HttpContext ctx, string project, int id) =>
        {
            var p = Find(model, project);
            var e = p is null ? null : InProject(p).FirstOrDefault(x => x.Id == id);
            if (e is null) return NotFound();
            var root = Root(ctx);
            return Results.Json(new Dictionary<string, object?>
            {
                ["createdBy"] = IdentityRef(model, root, null), ["createdOn"] = Iso(DefaultDate),
                ["lastModifiedBy"] = IdentityRef(model, root, null), ["lastModifiedOn"] = Iso(DefaultDate),
                ["id"] = e.Id, ["name"] = e.Name, ["description"] = e.Description ?? "", ["resources"] = Array.Empty<object>()
            });
        });

        c.MapGet("/{project}/_apis/pipelines/checks/configurations", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var root = Root(ctx);
            var value = new List<object>();
            var expand = Q(ctx, "$expand")?.Contains("settings", StringComparison.OrdinalIgnoreCase) == true;
            if (Q(ctx, "resourceType")?.Equals("environment", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                var resourceId = Q(ctx, "resourceId");
                foreach (var e in InProject(p).Where(e => resourceId is null || e.Id.ToString() == resourceId))
                    for (var i = 0; i < (e.Checks?.Count ?? 0); i++)
                        value.Add(CheckConfiguration(model, root, p, e, e.Checks![i], i, expand));
            }
            return Results.Json(new { count = value.Count, value });
        });
    }

    // ---- branch policies ---------------------------------------------------------------------------

    private static (string Id, string Name) PolicyType(string type) => type.ToLowerInvariant() switch
    {
        "minreviewers" => ("fa4e907d-c16b-4a4c-9dfa-4906e5d171dd", "Minimum number of reviewers"),
        "build" => ("0609b952-1397-4640-95ec-e00a01b2c241", "Build"),
        "workitemlinking" => ("40e92b44-2fe1-4dd6-b3d8-74a9c21d0c6e", "Work item linking"),
        "commentresolution" => ("c6a1889d-b943-4856-b76f-9e46bb6b0df2", "Comment requirements"),
        "requiredreviewers" => ("fd2167ab-b0be-447a-8ec8-39368250530e", "Required reviewers"),
        _ => (Ids.Guid("policy-type:" + type), type)
    };

    private static object Policy(SimModel model, string root, SimProject p, SeedPolicy x)
    {
        var repo = x.Repo is null ? null : p.Repos.FirstOrDefault(r => r.Name == x.Repo);
        var scope = new Dictionary<string, object?> { ["repositoryId"] = repo?.Id };
        if (x.Branch is not null && repo is not null)
        {
            scope["refName"] = FullRef(x.Branch);
            scope["matchKind"] = "Exact";
        }
        var settings = new Dictionary<string, object?>();
        switch (x.Type.ToLowerInvariant())
        {
            case "minreviewers":
                settings["minimumApproverCount"] = x.Min ?? 1; settings["creatorVoteCounts"] = false; settings["allowDownvotes"] = false;
                settings["resetOnSourcePush"] = true; settings["requireVoteOnLastIteration"] = false;
                settings["resetRejectionsOnSourcePush"] = false; settings["blockLastPusherVote"] = false;
                break;
            case "build":
                settings["buildDefinitionId"] = x.Pipeline; settings["queueOnSourceUpdateOnly"] = true; settings["manualQueueOnly"] = false;
                settings["displayName"] = "Build validation"; settings["validDuration"] = 720.0;
                break;
            case "requiredreviewers":
                settings["requiredReviewerIds"] = (x.Reviewers ?? []).Select(r => model.FindByName(r)?.Id ?? Ids.Guid("identity:" + r)).ToArray();
                settings["filenamePatterns"] = Array.Empty<string>(); settings["addedFilesOnly"] = false;
                settings["message"] = "Required reviewers";
                break;
        }
        settings["scope"] = new[] { scope };
        var (typeId, typeName) = PolicyType(x.Type);
        var url = $"{root}/{p.Id}/_apis/policy/configurations/{x.Id}";
        return new
        {
            createdBy = IdentityRef(model, root, x.CreatedBy), createdDate = Iso(DefaultDate), isEnabled = x.Enabled,
            isBlocking = x.Blocking, isDeleted = false, settings, isEnterpriseManaged = false, _links = new { self = Link(url) },
            revision = 1, id = x.Id, url, type = new { id = typeId, url = $"{root}/{p.Id}/_apis/policy/types/{typeId}", displayName = typeName }
        };
    }

    private static void MapPolicies(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/{project}/_apis/policy/configurations", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            IEnumerable<SeedPolicy> all = (model.Seed.Policies ?? []).Where(x => x.Project == p.Name);
            if (Q(ctx, "repositoryId") is { Length: > 0 } repoId)
                all = all.Where(x => p.Repos.FirstOrDefault(r => r.Name == x.Repo)?.Id == repoId);
            if (Q(ctx, "refName") is { Length: > 0 } refName) all = all.Where(x => x.Branch is not null && FullRef(x.Branch) == refName);
            if (Q(ctx, "policyType") is { Length: > 0 } policyType) all = all.Where(x => PolicyType(x.Type).Id.Equals(policyType, StringComparison.OrdinalIgnoreCase));
            var root = Root(ctx);
            return Page(ctx, all.OrderBy(x => x.Id).ToList(), 1000, x => Policy(model, root, p, x));
        });

        c.MapGet("/{project}/_apis/policy/configurations/{id:int}", (HttpContext ctx, string project, int id) =>
        {
            var p = Find(model, project);
            var x = (model.Seed.Policies ?? []).FirstOrDefault(y => y.Id == id && y.Project == p?.Name);
            return x is null ? NotFound() : Results.Json(Policy(model, Root(ctx), p!, x));
        });
    }

    // ---- service hooks -----------------------------------------------------------------------------

    private static (string Consumer, string Action) HookConsumer(SeedHook h) => h.Consumer.ToLowerInvariant() switch
    {
        "slack" => ("slack", "postMessageToChannel"),
        _ => ("webHooks", "httpRequest")
    };

    private static object Hook(SimModel model, string root, SeedHook h)
    {
        var id = Ids.Guid("hook:" + h.Name);
        var project = model.ProjectByName(h.Project);
        var repo = project?.Repos.FirstOrDefault(r => r.Name == h.Repo);
        var publisherInputs = new Dictionary<string, string?>
        {
            ["hostId"] = Ids.Guid("host:" + model.Seed.Collection), ["tfsSubscriptionId"] = Ids.Guid("tfs-subscription:" + h.Name)
        };
        if (project is not null) publisherInputs["projectId"] = project.Id;
        if (repo is not null) publisherInputs["repository"] = repo.Id;
        if (h.Branch is not null) publisherInputs[h.Event == "git.pullrequest.created" ? "targetBranch" : "branch"] = FullRef(h.Branch);
        if (h.Event == "build.complete") publisherInputs["buildStatus"] = "";
        var (consumerId, actionId) = HookConsumer(h);
        var secretTarget = h.Consumer.Equals("slack", StringComparison.OrdinalIgnoreCase) || h.Consumer.Equals("teams", StringComparison.OrdinalIgnoreCase);
        var description = h.Event switch
        {
            "git.push" => $"Repository: {h.Repo ?? "[Any]"}, Branch: {(h.Branch is null ? "[Any]" : h.Branch)}",
            "git.pullrequest.created" => $"Repository: {h.Repo ?? "[Any]"}, Target branch: {(h.Branch is null ? "[Any]" : h.Branch)}",
            _ => "Build status: [Any]"
        };
        var user = IdentityRef(model, root, h.CreatedBy);
        return new
        {
            id, url = $"{root}/_apis/hooks/subscriptions/{id}", status = h.Enabled ? "enabled" : "disabledByUser", publisherId = "tfs",
            eventType = h.Event, resourceVersion = "1.0", eventDescription = description, consumerId, consumerActionId = actionId,
            actionDescription = consumerId == "slack" ? "Post a message to a Slack channel" : "To host " + new Uri(h.Url ?? "https://localhost").Host,
            createdBy = user, createdDate = Iso(DefaultDate), modifiedBy = user, modifiedDate = Iso(DefaultDate), probationRetries = 0,
            publisherInputs, consumerInputs = new { url = secretTarget ? "********" : h.Url }
        };
    }

    private static void MapHooks(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/_apis/hooks/subscriptions", (HttpContext ctx) =>
        {
            IEnumerable<SeedHook> all = model.Seed.Hooks ?? [];
            if (Q(ctx, "publisherId") is { Length: > 0 } publisher && !publisher.Equals("tfs", StringComparison.OrdinalIgnoreCase)) all = [];
            if (Q(ctx, "eventType") is { Length: > 0 } eventType) all = all.Where(h => h.Event.Equals(eventType, StringComparison.OrdinalIgnoreCase));
            if (Q(ctx, "consumerId") is { Length: > 0 } consumer) all = all.Where(h => HookConsumer(h).Consumer.Equals(consumer, StringComparison.OrdinalIgnoreCase));
            if (Q(ctx, "consumerActionId") is { Length: > 0 } action) all = all.Where(h => HookConsumer(h).Action.Equals(action, StringComparison.OrdinalIgnoreCase));
            var root = Root(ctx);
            var value = all.Select(h => Hook(model, root, h)).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/_apis/hooks/subscriptions/{id}", (HttpContext ctx, string id) =>
        {
            var h = (model.Seed.Hooks ?? []).FirstOrDefault(x => Ids.Guid("hook:" + x.Name).Equals(id, StringComparison.OrdinalIgnoreCase));
            return h is null ? NotFound() : Results.Json(Hook(model, Root(ctx), h));
        });
    }
}
