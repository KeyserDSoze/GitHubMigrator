using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace AdoSim;

public sealed record SimOptions(
    SeedSpec Seed, string DataDir, string Pat = "sim-pat", string MaxApiVersion = "7.0",
    int RefsPageSize = 1000, string Urls = "http://0.0.0.0:8080");

// Minimal Azure DevOps Server 2022-style REST surface plus read-only Git smart HTTP,
// backed by real bare repositories so downstream tests exercise real refs and SHAs.
public static class AdoSimApp
{
    public static async Task<WebApplication> StartAsync(SimOptions options)
    {
        Directory.CreateDirectory(options.DataDir);
        var model = new SimModel(options.Seed, options.DataDir);
        foreach (var repo in model.Projects.SelectMany(p => p.Repos))
            await Git.CreateRepositoryAsync(repo, options.DataDir);
        var expected = await ExpectedAsync(model);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.Use(async (context, next) => await Gate(context, next, options));

        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapGet("/_sim/expected", () => Results.Json(expected));

        var c = app.MapGroup("/tfs/{collection}");
        c.MapGet("/_apis/connectionData", (HttpContext ctx) => Results.Json(new
        {
            authenticatedUser = new { id = Ids.Guid("auth"), providerDisplayName = "Sim Administrator" },
            deploymentType = "onPremises"
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

        c.MapGet("/{project}/_apis/git/repositories", (string project) =>
        {
            var p = Find(model, project);
            if (p is null) return NotFound();
            var value = p.Repos.Select(r => new { id = r.Id, name = r.Name, isDisabled = r.Spec.Disabled, project = new { id = p.Id, name = p.Name } }).ToArray();
            return Results.Json(new { count = value.Length, value });
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

        c.MapGet("/{project}/_apis/git/repositories/{repo}/items", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            var path = Q(ctx, "path");
            if (r is null || path is null) return NotFound();
            var content = await Git.ShowFileAsync(r, path);
            return content is null ? NotFound() : Results.Json(new { path, content });
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
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}/tfs/{options.Seed.Collection}";
            var list = new List<object>();
            foreach (var w in (options.Seed.WorkItems ?? []).Where(w => ids.Contains(w.Id)))
                list.Add(await WorkItem(model, w, origin));
            return Results.Json(new { count = list.Count, value = list });
        });

        c.MapGet("/{project}/_apis/pipelines/{id:int}", (string project, int id) =>
        {
            var (p, pipeline) = FindPipeline(model, project, id);
            if (pipeline is null) return NotFound();
            var repo = p!.Repos.First(r => r.Name == pipeline.Repo);
            return Results.Json(new
            {
                id, name = pipeline.Name,
                configuration = new { type = "yaml", path = pipeline.Path, repository = new { id = repo.Id } }
            });
        });

        c.MapGet("/{project}/_apis/build/definitions/{id:int}", (string project, int id) =>
        {
            var (_, pipeline) = FindPipeline(model, project, id);
            if (pipeline is null) return NotFound();
            var variables = (pipeline.Variables ?? []).ToDictionary(v => v.Name,
                v => (object)(v.Secret ? new { isSecret = true } : new { value = "sim-value" }));
            return Results.Json(new { variables, variableGroups = pipeline.VariableGroups ?? [] });
        });

        c.MapGet("/{project}/_apis/distributedtask/variablegroups/{id:int}", (int id) =>
        {
            var g = options.Seed.VariableGroups?.FirstOrDefault(x => x.Id == id);
            return g is null ? NotFound() : Results.Json(new
            {
                name = g.Name,
                variables = g.Variables.ToDictionary(v => v.Name, v => (object)(v.Secret ? new { isSecret = true } : new { value = "sim-value" }))
            });
        });

        c.MapGet("/{project}/_apis/serviceendpoint/endpoints", () =>
        {
            var value = (options.Seed.ServiceEndpoints ?? []).Select(e => new { e.Name, e.Type }).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        MapGitSmartHttp(app, model);
        await app.StartAsync();
        return app;
    }

    private static void MapGitSmartHttp(WebApplication app, SimModel model)
    {
        const string prefix = "/tfs/{collection}/{project}/_git/{repo}";
        app.MapGet(prefix + "/info/refs", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null || Q(ctx, "service") != "git-upload-pack") return Results.StatusCode(403);
            ctx.Response.ContentType = "application/x-git-upload-pack-advertisement";
            await Git.WriteAsync(ctx.Response.Body, "001e# service=git-upload-pack\n0000");
            await StreamGit(ctx, r, null, "upload-pack", "--stateless-rpc", "--advertise-refs", r.BarePath);
            return Results.Empty;
        });
        app.MapPost(prefix + "/git-upload-pack", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return Results.StatusCode(404);
            ctx.Response.ContentType = "application/x-git-upload-pack-result";
            Stream input = ctx.Request.Body;
            if (ctx.Request.Headers.ContentEncoding == "gzip") input = new GZipStream(input, CompressionMode.Decompress);
            await StreamGit(ctx, r, input, "upload-pack", "--stateless-rpc", r.BarePath);
            return Results.Empty;
        });
    }

    private static async Task StreamGit(HttpContext ctx, SimRepo repo, Stream? input, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        { RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, ctx.RequestAborted);
            process.StandardInput.Close();
        }
        await process.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        await process.WaitForExitAsync(ctx.RequestAborted);
    }

    private static async Task Gate(HttpContext ctx, RequestDelegate next, SimOptions options)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/tfs/", StringComparison.Ordinal))
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

    private static string? Q(HttpContext ctx, string name) =>
        ctx.Request.Query.TryGetValue(name, out StringValues v) && v.Count > 0 ? v[0] : null;
    private static int? Int(HttpContext ctx, string name) => int.TryParse(Q(ctx, name), out var v) ? v : null;
    private static IResult NotFound() => Results.Json(new { message = "Not found" }, statusCode: 404);

    private static SimProject? Find(SimModel m, string idOrName) =>
        m.Projects.FirstOrDefault(p => p.Id == idOrName || p.Name == Uri.UnescapeDataString(idOrName));

    private static SimRepo? FindRepo(SimModel m, string project, string repo) =>
        Find(m, project)?.Repos.FirstOrDefault(r => r.Id == repo || r.Name == Uri.UnescapeDataString(repo));

    private static (SimProject?, SeedPipeline?) FindPipeline(SimModel m, string project, int id)
    {
        var p = Find(m, project);
        return (p, p?.Spec.Pipelines?.FirstOrDefault(x => x.Id == id));
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
                    var tail = link.Kind == "commit" ? await Git.RevParseAsync(repo, link.Ref ?? "HEAD") : link.To?.ToString();
                    if (tail is null) break;
                    var artifact = link.Kind == "commit" ? "Commit" : "PullRequestId";
                    relations.Add(new { rel = "ArtifactLink", url = $"vstfs:///Git/{artifact}/{project.Id}%2f{repo.Id}%2f{tail}" });
                    break;
            }
        }
        return new
        {
            id = w.Id, rev = 1,
            fields = new Dictionary<string, object?>
            {
                ["System.Title"] = w.Title, ["System.WorkItemType"] = w.Type, ["System.State"] = w.State,
                ["System.Description"] = w.Description, ["System.TeamProject"] = w.Project
            },
            relations
        };
    }

    private static async Task<object> ExpectedAsync(SimModel m)
    {
        var refs = 0;
        foreach (var repo in m.Projects.SelectMany(p => p.Repos).Where(r => !r.Spec.Disabled))
            refs += (await Git.RefsAsync(repo)).Count;
        var gitRepos = m.Projects.Where(p => !p.IsTfvc).Sum(p => p.Repos.Count);
        var tfvcRoots = m.Projects.Where(p => p.IsTfvc).Sum(p => (p.Spec.TfvcBranches ?? []).Append($"$/{p.Name}").Distinct().Count());
        return new
        {
            projects = m.Projects.Count, gitRepositories = gitRepos, tfvcRoots,
            refs, users = m.Identities.Count(i => !i.IsContainer), groups = m.Identities.Count(i => i.IsContainer),
            memberships = m.Identities.Where(i => i.IsContainer).Sum(i => i.MemberNames.Count),
            aclEntries = m.Projects.Where(p => !p.IsTfvc).Sum(p => p.Repos.Count * (p.Spec.Acl?.Count ?? 0) +
                p.Repos.Sum(r => r.Spec.Acl?.Count ?? 0)),
            // Durable jobs store each raw ACE once per security token, not once per repository.
            distinctAclEntries = m.Projects.Where(p => !p.IsTfvc && p.Repos.Count > 0)
                .Sum(p => (p.Spec.Acl?.Count ?? 0) + p.Repos.Sum(r => r.Spec.Acl?.Count ?? 0)),
            disabledRepositories = m.Projects.Sum(p => p.Repos.Count(r => r.Spec.Disabled))
        };
    }
}
