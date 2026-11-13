using System.Text;

namespace AdoSim;

public sealed record SimOptions(
    SeedSpec Seed, string DataDir, string Pat = "sim-pat", string MaxApiVersion = "7.0",
    int RefsPageSize = 1000, string Urls = "http://0.0.0.0:8080", string Flavor = SimFlavors.Server,
    int GraphPageSize = 100);

public static class SimFlavors
{
    public const string Server = "server";
    public const string Services = "services";
}

// Azure DevOps REST surface plus Git smart HTTP (fetch and push), backed by real bare repositories so
// downstream tests exercise real refs and SHAs. Two flavours of the same estate:
//   server   (default): Azure DevOps Server 2022, {origin}/tfs/{collection}, Identities API, api <= 7.0
//   services (stand-in): Azure DevOps Services, {origin}/{organization} for core and git, the Graph API
//            under {origin}/vssps/{organization} (a different host in production), api <= 7.1.
public static partial class AdoSimApp
{
    public static async Task<WebApplication> StartAsync(SimOptions options)
    {
        var model = new SimModel(options.Seed, options.DataDir);
        await Estate.EnsureAsync(model);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
        var app = builder.Build();
        app.Use(async (context, next) => await Gate(context, next, options));

        app.MapGet("/healthz", () => Results.Text("ok"));
        MapSimControl(app, model);

        var services = options.Flavor == SimFlavors.Services;
        var c = app.MapGroup(services ? "/{collection}" : "/tfs/{collection}");
        MapCore(c, model, options);
        MapPipelines(c, model);
        MapIntegrations(c, model);
        MapGitApi(c, model);
        if (services) MapServicesGraph(app.MapGroup("/vssps/{collection}"), model, options);
        MapGitSmartHttp(app, model, services ? "" : "/tfs");
        await app.StartAsync();
        return app;
    }

    private static void MapCore(RouteGroupBuilder c, SimModel model, SimOptions options)
    {
        c.MapGet("/_apis/connectionData", (HttpContext ctx) => Results.Json(new
        {
            authenticatedUser = new { id = Ids.Guid("auth"), providerDisplayName = "Sim Administrator" },
            deploymentType = options.Flavor == SimFlavors.Services ? "hosted" : "onPremises"
        }));

        c.MapGet("/_apis/projects", (HttpContext ctx) =>
        {
            var top = Int(ctx, "$top") ?? 100; var skip = Int(ctx, "$skip") ?? 0;
            var page = model.Projects.Skip(skip).Take(top).Select(p => new { id = p.Id, name = p.Name, state = "wellFormed" }).ToArray();
            return Results.Json(new { count = page.Length, value = page });
        });

        c.MapGet("/_apis/projects/{project}", (string project) =>
        {
            var p = Find(model, project);
            return p is null ? NotFound() : Results.Json(new
            {
                id = p.Id, name = p.Name,
                capabilities = new { versioncontrol = new { sourceControlType = p.IsTfvc ? "Tfvc" : "Git" } }
            });
        });

        c.MapGet("/{project}/_apis/git/repositories", (HttpContext ctx, string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var root = Root(ctx);
            var value = p.Repos.Select(r => new
            {
                id = r.Id, name = r.Name, isDisabled = r.Spec.Disabled, project = new { id = p.Id, name = p.Name },
                defaultBranch = r.Spec.Empty || r.Spec.Commits < 1 ? null : r.DefaultRef,
                url = $"{root}/{p.Id}/_apis/git/repositories/{r.Id}", remoteUrl = RemoteUrl(root, r), size = RepoSize(r)
            }).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var root = Root(ctx);
            var hasCommits = (await Git.RefsAsync(r)).Count > 0;
            return Results.Json(new
            {
                id = r.Id, name = r.Name, url = $"{root}/{r.ProjectId}/_apis/git/repositories/{r.Id}",
                project = ProjectRef(root, Find(model, project)!), defaultBranch = hasCommits ? r.DefaultRef : null,
                remoteUrl = RemoteUrl(root, r), webUrl = RemoteUrl(root, r), isDisabled = r.Spec.Disabled, size = RepoSize(r),
                isFork = false
            });
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/refs", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            if (r.Spec.Disabled) return Results.Json(new { message = "TF401019: repository is disabled" }, statusCode: 403);
            var top = Math.Min(Int(ctx, "$top") ?? options.RefsPageSize, options.RefsPageSize);
            var offset = Int(ctx, "continuationToken") ?? 0;
            var refs = await Git.RefsAsync(r);
            var page = refs.Skip(offset).Take(top).Select(x => new { name = x.Name, objectId = x.ObjectId }).ToArray();
            if (offset + top < refs.Count) ctx.Response.Headers["X-MS-ContinuationToken"] = (offset + top).ToString();
            return Results.Json(new { count = page.Length, value = page });
        });

        c.MapGet("/{project}/_apis/tfvc/branches", (string project) =>
        {
            var p = Find(model, project);
            if (p is null || !p.IsTfvc) return NotFound();
            var all = p.Spec.TfvcBranches ?? [];
            object Node(string path) => new
            {
                path,
                children = all.Where(x => x.StartsWith(path + "/", StringComparison.Ordinal) &&
                    x.IndexOf('/', path.Length + 1) < 0).Select(Node).ToArray()
            };
            var roots = all.Where(x => !all.Any(o => x != o && x.StartsWith(o + "/", StringComparison.Ordinal))).Select(Node).ToArray();
            return Results.Json(new { count = roots.Length, value = roots });
        });

        c.MapGet("/_apis/identities", (HttpContext ctx) =>
        {
            var membership = (Q(ctx, "queryMembership") ?? "None") != "None";
            var descriptors = Q(ctx, "descriptors");
            IEnumerable<SimIdentity> selected = descriptors is null
                ? model.Identities
                : descriptors.Split(',').Select(model.FindByDescriptor).OfType<SimIdentity>();
            var value = selected.Select(i => Identity(model, i, membership)).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/_apis/accesscontrollists/{ns}", (HttpContext ctx) =>
        {
            var token = Q(ctx, "token") ?? "";
            var parts = token.Split('/');
            if (parts.Length < 2 || parts[0] != "repoV2") return Results.Json(new { count = 0, value = Array.Empty<object>() });
            var p = model.Projects.FirstOrDefault(x => x.Id == parts[1]);
            if (p is null) return Results.Json(new { count = 0, value = Array.Empty<object>() });
            List<SeedAce>? aces; var inherit = true;
            if (parts.Length == 2) aces = p.Spec.Acl;
            else
            {
                var r = p.Repos.FirstOrDefault(x => x.Id == parts[2]);
                aces = r?.Spec.Acl; inherit = r?.Spec.InheritPermissions ?? true;
            }
            if (aces is null || aces.Count == 0) return Results.Json(new { count = 0, value = Array.Empty<object>() });
            var dictionary = aces.ToDictionary(a => model.DescriptorFor(a.Principal),
                a => (object)new { descriptor = model.DescriptorFor(a.Principal), allow = a.Allow, deny = a.Deny });
            return Results.Json(new { count = 1, value = new[] { new { token, inheritPermissions = inherit, acesDictionary = dictionary } } });
        });

        c.MapGet("/{project}/_apis/wit/workitems", async (HttpContext ctx, string project) =>
        {
            var ids = (Q(ctx, "ids") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
            var origin = Root(ctx);
            var list = new List<object>();
            foreach (var w in (options.Seed.WorkItems ?? []).Where(w => ids.Contains(w.Id)))
                list.Add(await WorkItem(model, w, origin));
            return Results.Json(new { count = list.Count, value = list });
        });
    }

    private static object Identity(SimModel m, SimIdentity i, bool membership)
    {
        var props = new Dictionary<string, object>
        {
            ["Account"] = new Dictionary<string, object?> { ["$type"] = "System.String", ["$value"] = i.Name }
        };
        if (i.Mail is not null)
            props["Mail"] = new Dictionary<string, object?> { ["$type"] = "System.String", ["$value"] = i.Mail };
        var members = membership
            ? i.MemberNames.Select(n => m.FindByName(n)?.Descriptor ?? n).ToArray() : [];
        return new
        {
            id = i.Id, descriptor = i.Descriptor, providerDisplayName = i.Display, isActive = true,
            isContainer = i.IsContainer, members, properties = props
        };
    }

    private static async Task<object> WorkItem(SimModel m, SeedWorkItem w, string origin)
    {
        var relations = new List<object>();
        foreach (var link in w.Links ?? [])
        {
            switch (link.Kind)
            {
                case "child": relations.Add(new { rel = "System.LinkTypes.Hierarchy-Forward", url = $"{origin}/_apis/wit/workItems/{link.To}" }); break;
                case "parent": relations.Add(new { rel = "System.LinkTypes.Hierarchy-Reverse", url = $"{origin}/_apis/wit/workItems/{link.To}" }); break;
                case "related": relations.Add(new { rel = "System.LinkTypes.Related", url = $"{origin}/_apis/wit/workItems/{link.To}" }); break;
                case "commit":
                case "pullrequest":
                    var project = m.Projects.First(p => p.Name == w.Project);
                    var repo = project.Repos.FirstOrDefault(r => r.Name == link.Repo);
                    if (repo is null) break;
                    var tail = link.Kind == "commit" ? await Git.RevParseAsync(repo, (link.Ref ?? "HEAD") + "^{commit}") : link.To?.ToString();
                    if (tail is null) break;
                    var artifact = link.Kind == "commit" ? "Commit" : "PullRequestId";
                    relations.Add(new { rel = "ArtifactLink", url = $"vstfs:///Git/{artifact}/{project.Id}%2f{repo.Id}%2f{tail}" });
                    break;
            }
        }
        var fields = new Dictionary<string, object?>
        {
            ["System.Title"] = w.Title, ["System.WorkItemType"] = w.Type, ["System.State"] = w.State,
            ["System.Description"] = w.Description, ["System.TeamProject"] = w.Project
        };
        if (w.AssignedTo is not null)
        {
            var user = m.FindByName(w.AssignedTo);
            fields["System.AssignedTo"] = new { displayName = user?.Display ?? w.AssignedTo, uniqueName = "SIM\\" + w.AssignedTo, id = user?.Id };
        }
        if (w.Tags is not null) fields["System.Tags"] = w.Tags;
        return new { id = w.Id, rev = 1, fields, relations };
    }

    private static async Task<object> ExpectedAsync(SimModel m)
    {
        var refCounts = new int[m.Repos.Count(r => !r.Spec.Disabled)];
        var active = m.Repos.Where(r => !r.Spec.Disabled).ToList();
        await Parallel.ForEachAsync(Enumerable.Range(0, active.Count), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, _) => refCounts[i] = (await Git.RefsAsync(active[i])).Count);
        var seed = m.Seed;
        var gitRepos = m.Projects.Where(p => !p.IsTfvc).Sum(p => p.Repos.Count);
        var tfvcRoots = m.Projects.Where(p => p.IsTfvc).Sum(p => (p.Spec.TfvcBranches ?? []).Append($"$/{p.Name}").Distinct().Count());
        var pipelines = m.Pipelines.ToList();
        return new
        {
            projects = m.Projects.Count, gitRepositories = gitRepos, tfvcRoots,
            refs = refCounts.Sum(), users = m.Identities.Count(i => !i.IsContainer), groups = m.Identities.Count(i => i.IsContainer),
            memberships = m.Identities.Where(i => i.IsContainer).Sum(i => i.MemberNames.Count),
            aclEntries = m.Projects.Where(p => !p.IsTfvc).Sum(p => p.Repos.Count * (p.Spec.Acl?.Count ?? 0) +
                p.Repos.Sum(r => r.Spec.Acl?.Count ?? 0)),
            // Durable jobs store each raw ACE once per security token, not once per repository.
            distinctAclEntries = m.Projects.Where(p => !p.IsTfvc && p.Repos.Count > 0)
                .Sum(p => (p.Spec.Acl?.Count ?? 0) + p.Repos.Sum(r => r.Spec.Acl?.Count ?? 0)),
            disabledRepositories = m.Projects.Sum(p => p.Repos.Count(r => r.Spec.Disabled)),
            yamlPipelines = pipelines.Count(x => x.Pipeline.Classic is null),
            classicPipelines = pipelines.Count(x => x.Pipeline.Classic is not null),
            serviceEndpoints = seed.ServiceEndpoints?.Count ?? 0,
            variableGroups = seed.VariableGroups?.Count ?? 0,
            environments = seed.Environments?.Count ?? 0,
            policies = seed.Policies?.Count ?? 0,
            hooks = seed.Hooks?.Count ?? 0,
            pullRequests = seed.PullRequests?.Count ?? 0,
            runs = seed.Runs?.Count ?? 0
        };
    }

    private static async Task Gate(HttpContext ctx, RequestDelegate next, SimOptions options)
    {
        var path = ctx.Request.Path.Value ?? "";
        // Services: the organization is the first path segment, so everything except health and the sim
        // control plane (which has its own rules below) is protected.
        var protectedPath = options.Flavor == SimFlavors.Services
            ? path != "/healthz" && !path.StartsWith("/_sim/", StringComparison.Ordinal) ||
                path.StartsWith("/_sim/repos/", StringComparison.Ordinal) || path == "/_sim/reset"
            : path.StartsWith("/tfs/", StringComparison.Ordinal) ||
                path.StartsWith("/_sim/repos/", StringComparison.Ordinal) || path == "/_sim/reset";
        if (protectedPath)
        {
            if (!Authorized(ctx, options.Pat))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"AdoSim\"";
                return;
            }
            var version = Q(ctx, "api-version");
            if (path.Contains("/_apis/", StringComparison.Ordinal) && version is not null && !path.EndsWith("/connectionData") &&
                !VersionAllowed(version, options.MaxApiVersion))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    message = $"The requested version \"{version}\" of the resource is under preview. The latest stable version of the resource is \"{options.MaxApiVersion}\".",
                    typeName = "Microsoft.VisualStudio.Services.WebApi.VssInvalidPreviewVersionException, Microsoft.VisualStudio.Services.WebApi"
                });
                return;
            }
        }
        await next(ctx);
    }

    private static bool Authorized(HttpContext ctx, string pat)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            return decoded[(decoded.IndexOf(':') + 1)..] == pat;
        }
        catch (FormatException) { return false; }
    }

    private static bool VersionAllowed(string requested, string max)
    {
        static decimal Parse(string v) => decimal.TryParse(v.Split('-')[0], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        var value = Parse(requested);
        return value >= 5.0m && value <= Parse(max);
    }
}
