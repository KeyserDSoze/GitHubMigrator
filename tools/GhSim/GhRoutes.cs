using System.Text;
using System.Text.RegularExpressions;

namespace GhSim;

public static partial class GhSimApp
{
    private static readonly Regex RepoNamePattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex ShaPattern = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled);

    private static void MapRest(RouteGroupBuilder api, GhSim sim)
    {
        MapAccounts(api, sim);
        MapRepositories(api, sim);
        MapGitData(api, sim);
        MapContents(api, sim);
        MapActions(api, sim);
        MapCollaboration(api, sim);
        MapRules(api, sim);
        MapApps(api, sim);
    }

    // 404 for anything the caller cannot see, 403 only when the caller can see it but cannot write.
    private static (GhRepo? Repo, IResult? Error) Open(GhSim sim, HttpContext ctx, string org, string name, bool write = false)
    {
        var who = GhSim.Auth(ctx);
        GhRepo? repo;
        lock (sim.Store.Lock) repo = sim.Store.Repo(org, name);
        if (repo is null || !sim.CanRead(who, repo)) return (null, NotFound());
        if (write && !sim.CanWrite(who, repo))
            return (null, Error(403, repo.Archived ? "Repository was archived so is read-only." : "Resource not accessible by integration"));
        return (repo, null);
    }

    private static void MapAccounts(RouteGroupBuilder api, GhSim sim)
    {
        api.MapGet("/user", (HttpContext ctx) =>
        {
            var who = GhSim.Auth(ctx);
            if (who.User is null) return Error(403, "Resource not accessible by integration");
            return Results.Json(GhJson.Account(sim, ctx, who.User.Login, who.User.Id, "User"));
        });

        api.MapGet("/orgs/{org}", (HttpContext ctx, string org) =>
        {
            var who = GhSim.Auth(ctx);
            GhOrg? o; lock (sim.Store.Lock) o = sim.Store.Org(org);
            if (o is null) return NotFound();
            var d = (Dictionary<string, object?>)GhJson.Account(sim, ctx, o.Login, o.Id, "Organization");
            d["description"] = null;
            if (who.User is not null && sim.IsOrgMember(who.User, o))
            {
                d["members_can_create_repositories"] = true;
                d["members_can_create_private_repositories"] = o.MembersCanCreatePrivateRepositories;
            }
            return Results.Json(d);
        });

        api.MapGet("/user/memberships/orgs/{org}", (HttpContext ctx, string org) =>
        {
            var who = GhSim.Auth(ctx);
            GhOrg? o; lock (sim.Store.Lock) o = sim.Store.Org(org);
            if (o is null || who.User is null || !sim.IsOrgMember(who.User, o)) return NotFound();
            return Results.Json(new
            {
                url = $"{Api(ctx)}/orgs/{o.Login}/memberships/{who.User.Login}", state = "active",
                role = sim.IsOrgAdmin(who.User, o) ? "admin" : "member",
                organization_url = $"{Api(ctx)}/orgs/{o.Login}",
                organization = GhJson.Account(sim, ctx, o.Login, o.Id, "Organization"),
                user = GhJson.Account(sim, ctx, who.User.Login, who.User.Id, "User")
            });
        });

        api.MapGet("/repositories/{id:long}", (HttpContext ctx, long id) =>
        {
            GhRepo? repo; lock (sim.Store.Lock) repo = sim.Store.Repo(id);
            var who = GhSim.Auth(ctx);
            return repo is null || !sim.CanRead(who, repo) ? NotFound() : Results.Json(GhJson.Repo(sim, ctx, repo, who));
        });
    }

    private static void MapRepositories(RouteGroupBuilder api, GhSim sim)
    {
        api.MapGet("/orgs/{org}/repos", (HttpContext ctx, string org) =>
        {
            var who = GhSim.Auth(ctx);
            List<GhRepo> repos;
            lock (sim.Store.Lock)
                repos = sim.Store.State.Repos.Where(r => r.Org.Equals(org, StringComparison.OrdinalIgnoreCase) && sim.CanRead(who, r))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Json(Page(ctx, repos).Select(r => GhJson.Repo(sim, ctx, r, who)).ToArray());
        });

        api.MapPost("/orgs/{org}/repos", async (HttpContext ctx, string org) =>
        {
            var who = GhSim.Auth(ctx);
            GhOrg? o; lock (sim.Store.Lock) o = sim.Store.Org(org);
            if (o is null) return NotFound();
            if (who.User is null || !sim.IsOrgMember(who.User, o)) return Error(403, "Resource not accessible by integration");
            var body = await BodyAsync(ctx);
            var name = Str(body, "name");
            if (string.IsNullOrWhiteSpace(name)) return Invalid("name", "name is missing", "missing_field");
            name = Regex.Replace(name.Trim(), @"\s+", "-");
            if (!RepoNamePattern.IsMatch(name) || name is "." or ".." || name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                return Invalid("name", "name is invalid", "invalid");
            var visibility = Str(body, "visibility");
            var isPrivate = visibility is not null ? visibility != "public" : Bool(body, "private") ?? false;
            var isAdmin = sim.IsOrgAdmin(who.User, o);
            if (!isAdmin && (who.User.ReadOnly || (isPrivate && !o.MembersCanCreatePrivateRepositories)))
                return Error(403, "You need admin access to the organization before adding a repository to it.");
            lock (sim.Store.Lock)
                if (sim.Store.Repo(org, name) is not null)
                    return Error(422, "Repository creation failed.",
                        new { resource = "Repository", code = "custom", field = "name", message = "name already exists on this account" });
            var repo = await CreateRepositoryAsync(sim, o.Login, name, isPrivate, Str(body, "description"),
                Str(body, "default_branch") ?? "main", null, seeded: false);
            if (Bool(body, "auto_init") == true)
            {
                var identity = (who.User.Login, $"{who.User.Login}@users.noreply.sim.test");
                await RepoGit.CommitFilesAsync(sim.Store.BarePath(repo), repo.DefaultBranch,
                    [("README.md", Encoding.UTF8.GetBytes($"# {repo.Name}\n"))], "Initial commit", identity, identity, sim.Now);
            }
            ctx.Response.Headers.Location = $"{Api(ctx)}/repos/{repo.FullName}";
            return Results.Json(GhJson.Repo(sim, ctx, repo, who), statusCode: 201);
        });

        api.MapGet("/repos/{org}/{repo}", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            return error ?? Results.Json(GhJson.Repo(sim, ctx, r!, GhSim.Auth(ctx)));
        });

        api.MapPatch("/repos/{org}/{repo}", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var who = GhSim.Auth(ctx);
            if (!sim.CanAdmin(who, r!)) return Error(403, "Must have admin rights to Repository.");
            var body = await BodyAsync(ctx);
            var bare = sim.Store.BarePath(r!);
            var branch = Str(body, "default_branch");
            if (branch is not null && await RepoGit.RevParseAsync(bare, "refs/heads/" + branch) is null)
                return Invalid("default_branch", "default_branch is not an existing branch", "invalid");
            lock (sim.Store.Lock)
            {
                if (Str(body, "description") is { } description) r!.Description = description;
                if (Bool(body, "private") is { } isPrivate) r!.Private = isPrivate;
                if (Bool(body, "archived") is { } archived) r!.Archived = archived;
                if (branch is not null) r!.DefaultBranch = branch;
                r!.UpdatedAt = sim.Now;
                sim.Store.Save();
            }
            if (branch is not null)
            {
                await RepoGit.SetDefaultBranchAsync(bare, branch);
                await WriteRulesAsync(sim, r!);
            }
            return Results.Json(GhJson.Repo(sim, ctx, r!, who));
        });

        api.MapDelete("/repos/{org}/{repo}", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!sim.CanAdmin(GhSim.Auth(ctx), r!)) return Error(403, "Must have admin rights to Repository.");
            lock (sim.Store.Lock)
            {
                sim.Store.State.Repos.Remove(r!);
                sim.Store.Save();
            }
            GhStore.ForceDelete(sim.Store.BarePath(r!));
            return Results.NoContent();
        });

        api.MapGet("/repos/{org}/{repo}/branches", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var heads = (await RepoGit.RefsAsync(sim.Store.BarePath(r!))).Where(x => x.Name.StartsWith("refs/heads/")).ToList();
            return Results.Json(Page(ctx, heads).Select(h => new
            {
                name = h.Name["refs/heads/".Length..], commit = new { sha = h.ObjectId },
                @protected = GhSim.RulesetsFor(r!, h.Name).Any()
            }).ToArray());
        });

        api.MapGet("/repos/{org}/{repo}/branches/{**rest}", async (HttpContext ctx, string org, string repo, string rest) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            rest = Uri.UnescapeDataString(rest);
            if (rest.EndsWith("/protection", StringComparison.Ordinal))
            {
                var branch = rest[..^"/protection".Length];
                // This simulator models protection through rulesets only: classic protection is never visible.
                return branch.Length == 0 ? NotFound() : Error(404, "Branch not protected");
            }
            var sha = await RepoGit.RevParseAsync(sim.Store.BarePath(r!), "refs/heads/" + rest);
            return sha is null ? Error(404, "Branch not found") : Results.Json(new
            {
                name = rest, commit = new { sha }, @protected = GhSim.RulesetsFor(r!, "refs/heads/" + rest).Any()
            });
        });

        api.MapGet("/repos/{org}/{repo}/tags", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var bare = sim.Store.BarePath(r!);
            var tags = new List<object>();
            foreach (var t in (await RepoGit.RefsAsync(bare)).Where(x => x.Name.StartsWith("refs/tags/")))
            {
                var peeled = await RepoGit.RevParseAsync(bare, t.Name) ?? t.ObjectId;
                tags.Add(new { name = t.Name["refs/tags/".Length..], commit = new { sha = peeled } });
            }
            return Results.Json(Page(ctx, tags));
        });

        api.MapGet("/repos/{org}/{repo}/commits", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var bare = sim.Store.BarePath(r!);
            var start = Q(ctx, "sha") is { Length: > 0 } s ? s : r!.DefaultBranch;
            var (exit, output, _) = await GitCli.RunAsync(bare, null, "rev-list", "--max-count=100", start);
            if (exit != 0) return Error(409, "Git Repository is empty.");
            var shas = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            var commits = new List<object>();
            foreach (var sha in Page(ctx, shas))
                commits.Add(GhJson.RestCommit(ctx, r!, (await RepoGit.CommitAsync(bare, sha))!));
            return Results.Json(commits);
        });

        api.MapGet("/repos/{org}/{repo}/commits/{**sha}", async (HttpContext ctx, string org, string repo, string sha) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var c = await RepoGit.CommitAsync(sim.Store.BarePath(r!), Uri.UnescapeDataString(sha));
            return c is null ? Error(422, $"No commit found for SHA: {sha}") : Results.Json(GhJson.RestCommit(ctx, r!, c));
        });
    }

    // ---- Git data -----------------------------------------------------------------------

    private static void MapGitData(RouteGroupBuilder api, GhSim sim)
    {
        // matching-refs returns annotated tag objects as type "tag" (not the peeled commit).
        api.MapGet("/repos/{org}/{repo}/git/matching-refs/{**prefix}", async (HttpContext ctx, string org, string repo, string prefix) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var wanted = "refs/" + prefix;
            var refs = (await RepoGit.RefsAsync(sim.Store.BarePath(r!))).Where(x => x.Name.StartsWith(wanted, StringComparison.Ordinal)).ToList();
            return Results.Json(refs.Select(x => GhJson.GitRef(ctx, r!, x)).ToArray());
        });

        IResult RefResult(HttpContext ctx, GhRepo r, List<RefInfo> refs, string name, bool allowList)
        {
            var exact = refs.FirstOrDefault(x => x.Name == "refs/" + name);
            if (exact is not null) return Results.Json(GhJson.GitRef(ctx, r, exact));
            var partial = refs.Where(x => x.Name.StartsWith("refs/" + name + "/", StringComparison.Ordinal)).ToList();
            return allowList && partial.Count > 0
                ? Results.Json(partial.Select(x => GhJson.GitRef(ctx, r, x)).ToArray())
                : Error(404, "Not Found");
        }

        api.MapGet("/repos/{org}/{repo}/git/ref/{**name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            return error ?? RefResult(ctx, r!, await RepoGit.RefsAsync(sim.Store.BarePath(r!)), Uri.UnescapeDataString(name), false);
        });
        api.MapGet("/repos/{org}/{repo}/git/refs/{**name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            return error ?? RefResult(ctx, r!, await RepoGit.RefsAsync(sim.Store.BarePath(r!)), Uri.UnescapeDataString(name), true);
        });

        api.MapPost("/repos/{org}/{repo}/git/refs", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            var name = Str(body, "ref"); var sha = Str(body, "sha");
            if (name is null || !name.StartsWith("refs/") || sha is null || !ShaPattern.IsMatch(sha))
                return Error(422, "Validation Failed");
            var bare = sim.Store.BarePath(r!);
            if (await RepoGit.RevParseAsync(bare, sha) is null) return Error(422, "Object does not exist");
            using var _ = await Lock(sim, r!);
            if (await RepoGit.RevParseAsync(bare, name) is not null) return Error(422, "Reference already exists");
            if (await GhRules.CheckAsync(sim, r!, bare, name, RepoGit.ZeroOid, sha) is { } violation) return RuleViolation(violation, name);
            await RepoGit.UpdateRefAsync(bare, name, sha, null);
            Touch(sim, r!);
            return Results.Json(GhJson.GitRef(ctx, r!, new RefInfo(name, sha, await RepoGit.ObjectTypeAsync(bare, sha) ?? "commit")), statusCode: 201);
        });

        api.MapPatch("/repos/{org}/{repo}/git/refs/{**name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            var sha = Str(body, "sha"); var force = Bool(body, "force") ?? false;
            var full = "refs/" + Uri.UnescapeDataString(name);
            var bare = sim.Store.BarePath(r!);
            using var _ = await Lock(sim, r!);
            var current = await RepoGit.RevParseAsync(bare, full);
            if (current is null) return NotFound();
            if (sha is null || await RepoGit.RevParseAsync(bare, sha) is null) return Error(422, "Object does not exist");
            if (!force && !await RepoGit.IsAncestorAsync(bare, current, sha)) return Error(422, "Update is not a fast forward");
            if (await GhRules.CheckAsync(sim, r!, bare, full, current, sha) is { } violation) return RuleViolation(violation, full);
            await RepoGit.UpdateRefAsync(bare, full, sha, current);
            Touch(sim, r!);
            return Results.Json(GhJson.GitRef(ctx, r!, new RefInfo(full, sha, await RepoGit.ObjectTypeAsync(bare, sha) ?? "commit")));
        });

        api.MapDelete("/repos/{org}/{repo}/git/refs/{**name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var full = "refs/" + Uri.UnescapeDataString(name);
            var bare = sim.Store.BarePath(r!);
            using var _ = await Lock(sim, r!);
            var current = await RepoGit.RevParseAsync(bare, full);
            if (current is null) return Error(422, "Reference does not exist");
            if (await GhRules.CheckAsync(sim, r!, bare, full, current, RepoGit.ZeroOid) is { } violation) return RuleViolation(violation, full);
            await RepoGit.DeleteRefAsync(bare, full);
            Touch(sim, r!);
            return Results.NoContent();
        });

        api.MapGet("/repos/{org}/{repo}/git/commits/{sha}", async (HttpContext ctx, string org, string repo, string sha) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var c = ShaPattern.IsMatch(sha) ? await RepoGit.CommitAsync(sim.Store.BarePath(r!), sha) : null;
            return c is null || c.Sha != sha ? NotFound() : Results.Json(GhJson.GitCommit(ctx, r!, c));
        });

        api.MapGet("/repos/{org}/{repo}/git/tags/{sha}", async (HttpContext ctx, string org, string repo, string sha) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var t = ShaPattern.IsMatch(sha) ? await RepoGit.TagAsync(sim.Store.BarePath(r!), sha) : null;
            return t is null ? NotFound() : Results.Json(GhJson.GitTag(ctx, r!, t));
        });

        api.MapGet("/repos/{org}/{repo}/git/trees/{sha}", async (HttpContext ctx, string org, string repo, string sha) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var bare = sim.Store.BarePath(r!);
            if (!ShaPattern.IsMatch(sha) && await RepoGit.RevParseAsync(bare, sha) is null) return NotFound();
            var entries = await RepoGit.TreeAsync(bare, sha, null, Q(ctx, "recursive") is "1" or "true");
            return Results.Json(new
            {
                sha, url = $"{Api(ctx)}/repos/{r!.FullName}/git/trees/{sha}", truncated = false,
                tree = entries.Select(e => new { path = e.Path, mode = e.Mode, type = e.Type, sha = e.Sha }).ToArray()
            });
        });

        api.MapGet("/repos/{org}/{repo}/git/blobs/{sha}", async (HttpContext ctx, string org, string repo, string sha) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var data = ShaPattern.IsMatch(sha) ? await RepoGit.BlobAsync(sim.Store.BarePath(r!), sha) : null;
            return data is null ? NotFound() : Results.Json(new { sha, size = data.Length, encoding = "base64", content = Convert.ToBase64String(data) });
        });
    }

    private static async Task<IDisposable> Lock(GhSim sim, GhRepo repo)
    {
        var gate = sim.RepoLock(repo);
        await gate.WaitAsync();
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    private static void Touch(GhSim sim, GhRepo repo)
    {
        lock (sim.Store.Lock)
        {
            repo.PushedAt = sim.Now; repo.UpdatedAt = sim.Now;
            sim.Store.Save();
        }
    }

    private static IResult RuleViolation(string rule, string refName) =>
        Error(422, $"Repository rule violations found for {refName}: {rule}");

    // ---- contents -----------------------------------------------------------------------

    private static void MapContents(RouteGroupBuilder api, GhSim sim)
    {
        api.MapGet("/repos/{org}/{repo}/contents", (HttpContext ctx, string org, string repo) => ReadContents(sim, ctx, org, repo, ""));
        api.MapGet("/repos/{org}/{repo}/contents/{**path}", (HttpContext ctx, string org, string repo, string path) =>
            ReadContents(sim, ctx, org, repo, Uri.UnescapeDataString(path).Trim('/')));

        api.MapPut("/repos/{org}/{repo}/contents/{**path}", async (HttpContext ctx, string org, string repo, string path) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            path = Uri.UnescapeDataString(path).Trim('/');
            if (GhSim.IsWorkflowPath(path) && !GhSim.CanWriteWorkflowFiles(GhSim.Auth(ctx)))
                return Error(403, WorkflowRefusal(ctx, path));
            var body = await BodyAsync(ctx);
            var message = Str(body, "message"); var encoded = Str(body, "content");
            if (string.IsNullOrWhiteSpace(message) || encoded is null)
                return Error(422, "Invalid request.\n\n\"message\" wasn't supplied.");
            byte[] content;
            try { content = Convert.FromBase64String(encoded); }
            catch (FormatException) { return Error(422, "content is not valid Base64"); }
            return await WriteContentsAsync(sim, ctx, r!, path, content, message, Str(body, "branch"), Str(body, "sha"), body);
        });

        api.MapDelete("/repos/{org}/{repo}/contents/{**path}", async (HttpContext ctx, string org, string repo, string path) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            if (GhSim.IsWorkflowPath(Uri.UnescapeDataString(path).Trim('/')) && !GhSim.CanWriteWorkflowFiles(GhSim.Auth(ctx)))
                return Error(403, WorkflowRefusal(ctx, Uri.UnescapeDataString(path).Trim('/')));
            var body = await BodyAsync(ctx);
            var message = Str(body, "message");
            if (string.IsNullOrWhiteSpace(message) || Str(body, "sha") is null) return Error(422, "Invalid request.");
            return await WriteContentsAsync(sim, ctx, r!, Uri.UnescapeDataString(path).Trim('/'), null, message, Str(body, "branch"), Str(body, "sha"), body);
        });
    }

    // The wording of GitHub's refusal. The status code of the API answer (the sandbox assumes 403) is not confirmed against github.com.
    private static string WorkflowRefusal(HttpContext ctx, string path) =>
        $"refusing to allow {(GhSim.Auth(ctx).Kind == PrincipalKind.Installation ? "a GitHub App" : "a Personal Access Token")} to create or update workflow `{path}` without `{(GhSim.Auth(ctx).Kind == PrincipalKind.Installation ? "workflows" : "workflow")}` {(GhSim.Auth(ctx).Kind == PrincipalKind.Installation ? "permission" : "scope")}";

    private static (string Name, string Email) IdentityFrom(HttpContext ctx, System.Text.Json.JsonElement body, string key)
    {
        if (body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty(key, out var p) &&
            p.ValueKind == System.Text.Json.JsonValueKind.Object && Str(p, "name") is { } name && Str(p, "email") is { } email)
            return (name, email);
        var login = GhSim.Auth(ctx).Name;
        return (login, $"{login}@users.noreply.sim.test");
    }

    private static async Task<IResult> WriteContentsAsync(GhSim sim, HttpContext ctx, GhRepo repo, string path,
        byte[]? content, string message, string? branch, string? expectedSha, System.Text.Json.JsonElement body)
    {
        if (path.Length == 0 || path.Split('/').Any(s => s is "" or "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return Error(422, "path is invalid");
        branch ??= repo.DefaultBranch;
        var bare = sim.Store.BarePath(repo);
        using var _ = await Lock(sim, repo);
        var current = await RepoGit.RevParseAsync(bare, "refs/heads/" + branch);
        if (current is null && await RepoGit.HasHeadsAsync(bare)) return Error(404, $"Branch {branch} not found");
        var existing = current is null ? null : await RepoGit.RevParseAsync(bare, $"{current}:{path}");
        if (content is null && existing is null) return NotFound();
        if (content is not null && existing is not null && expectedSha is null)
            return Error(422, "Invalid request.\n\n\"sha\" wasn't supplied.");
        if (existing is not null && expectedSha is not null && expectedSha != existing)
            return Error(409, $"{path} does not match {expectedSha}");
        if (await GhRules.CheckAsync(sim, repo, bare, "refs/heads/" + branch, current ?? RepoGit.ZeroOid, "x") is { } violation)
            return RuleViolation(violation, "refs/heads/" + branch);

        var author = IdentityFrom(ctx, body, "author"); var committer = IdentityFrom(ctx, body, "committer");
        var (commitSha, blobSha) = await RepoGit.CommitFileAsync(bare, branch, path, content, message, author, committer, sim.Now);
        Touch(sim, repo);
        var commit = (await RepoGit.CommitAsync(bare, commitSha))!;
        var api = $"{Api(ctx)}/repos/{repo.FullName}";
        object? file = content is null ? null : new Dictionary<string, object?>
        {
            ["name"] = path.Split('/')[^1], ["path"] = path, ["sha"] = blobSha, ["size"] = content.Length, ["type"] = "file",
            ["url"] = $"{api}/contents/{path}?ref={branch}", ["html_url"] = $"{Origin(ctx)}/{repo.FullName}/blob/{branch}/{path}",
            ["git_url"] = $"{api}/git/blobs/{blobSha}", ["download_url"] = $"{Origin(ctx)}/{repo.FullName}/raw/{branch}/{path}"
        };
        return Results.Json(new { content = file, commit = GhJson.GitCommit(ctx, repo, commit) },
            statusCode: content is not null && existing is null ? 201 : 200);
    }

    private static async Task<IResult> ReadContents(GhSim sim, HttpContext ctx, string org, string repo, string path)
    {
        var (r, error) = Open(sim, ctx, org, repo);
        if (error is not null) return error;
        var bare = sim.Store.BarePath(r!);
        var reference = Q(ctx, "ref") is { Length: > 0 } q ? q : r!.DefaultBranch;
        var rev = await RepoGit.RevParseAsync(bare, "refs/heads/" + reference) ?? await RepoGit.RevParseAsync(bare, "refs/tags/" + reference)
            ?? await RepoGit.RevParseAsync(bare, reference);
        if (rev is null) return Error(404, "No commit found for the ref " + reference);
        var apiBase = $"{Api(ctx)}/repos/{r!.FullName}";

        object Entry(TreeEntry e) => new Dictionary<string, object?>
        {
            ["name"] = e.Path.Split('/')[^1], ["path"] = e.Path, ["sha"] = e.Sha, ["type"] = e.Type == "tree" ? "dir" : "file",
            ["url"] = $"{apiBase}/contents/{e.Path}?ref={reference}"
        };

        if (path.Length == 0)
            return Results.Json((await RepoGit.TreeAsync(bare, rev)).Select(Entry).ToArray());
        var type = await RepoGit.ObjectTypeAsync(bare, $"{rev}:{path}");
        if (type == "tree")
            return Results.Json((await RepoGit.TreeAsync(bare, rev, path + "/")).Select(Entry).ToArray());
        if (type != "blob") return NotFound();
        var sha = (await RepoGit.RevParseAsync(bare, $"{rev}:{path}"))!;
        var data = (await RepoGit.BlobAsync(bare, sha))!;
        if (ctx.Request.Headers.Accept.ToString().Contains("raw", StringComparison.OrdinalIgnoreCase))
            return Results.Bytes(data, "application/octet-stream");
        var b64 = Convert.ToBase64String(data);
        var wrapped = string.Join("\n", Enumerable.Range(0, (b64.Length + 59) / 60).Select(i => b64.Substring(i * 60, Math.Min(60, b64.Length - i * 60))));
        return Results.Json(new Dictionary<string, object?>
        {
            ["name"] = path.Split('/')[^1], ["path"] = path, ["sha"] = sha, ["size"] = data.Length, ["type"] = "file",
            ["encoding"] = "base64", ["content"] = wrapped + "\n", ["url"] = $"{apiBase}/contents/{path}?ref={reference}",
            ["git_url"] = $"{apiBase}/git/blobs/{sha}"
        });
    }
}
