using System.Text;
using System.Text.RegularExpressions;

namespace GhSim;

// Ruleset enforcement shared by REST writes and git pushes. Returns the violated rule type, or null.
public static class GhRules
{
    public static async Task<string?> CheckAsync(GhSim sim, GhRepo repo, string bare, string refName, string oldOid, string newOid)
    {
        var rules = GhSim.RulesetsFor(repo, refName).SelectMany(r => r.Rules).ToHashSet();
        if (rules.Count == 0) return null;
        var created = oldOid == RepoGit.ZeroOid; var deleted = newOid == RepoGit.ZeroOid;
        if (created && rules.Contains("creation")) return "creation";
        if (deleted) return rules.Contains("deletion") ? "deletion" : null;
        if (created) return null;
        if (rules.Contains("update")) return "update";
        if (rules.Contains("pull_request") && refName.StartsWith("refs/heads/")) return "pull_request";
        if (rules.Contains("non_fast_forward") && newOid != "x" && !await RepoGit.IsAncestorAsync(bare, oldOid, newOid))
            return "non_fast_forward";
        return null;
    }
}

public static partial class GhSimApp
{
    private static readonly Regex SecretName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static bool CanManageActions(HttpContext ctx, GhSim sim, GhRepo repo) =>
        GhSim.Auth(ctx).Kind == PrincipalKind.User && sim.CanWrite(GhSim.Auth(ctx), repo);

    private static IResult SecretKey(GhRepo r) => Results.Json(new { key_id = r.SecretKeyId, key = r.SecretKey });

    private static IResult? ValidateSecret(GhRepo repo, string name, string? keyId, string? encrypted)
    {
        if (!SecretName.IsMatch(name) || name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
            return Error(422, "Validation Failed", new { resource = "Secret", code = "invalid", field = "name", message = "name is invalid" });
        if (keyId != repo.SecretKeyId)
            return Error(422, "Validation Failed", new { resource = "Secret", code = "invalid", field = "key_id", message = "key_id was not the latest key" });
        try
        {
            // A libsodium sealed box has at least 48 bytes of overhead: plaintext uploads are rejected.
            if (encrypted is null || Convert.FromBase64String(encrypted).Length < 48) throw new FormatException();
        }
        catch (FormatException)
        {
            return Error(422, "Validation Failed", new { resource = "Secret", code = "invalid", field = "encrypted_value", message = "encrypted_value is not a valid sealed box" });
        }
        return null;
    }

    private static void MapActions(RouteGroupBuilder api, GhSim sim)
    {
        // ---- repository secrets ------------------------------------------------------
        api.MapGet("/repos/{org}/{repo}/actions/secrets", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            List<KeyValuePair<string, DateTimeOffset>> all;
            lock (sim.Store.Lock) all = r!.Secrets.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
            return Results.Json(new
            {
                total_count = all.Count,
                secrets = Page(ctx, all).Select(s => new { name = s.Key, created_at = Iso(s.Value), updated_at = Iso(s.Value) }).ToArray()
            });
        });

        api.MapGet("/repos/{org}/{repo}/actions/secrets/public-key", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            return error ?? (CanManageActions(ctx, sim, r!) ? SecretKey(r!) : Error(403, "Resource not accessible by integration"));
        });

        api.MapPut("/repos/{org}/{repo}/actions/secrets/{name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            var body = await BodyAsync(ctx);
            if (ValidateSecret(r!, name, Str(body, "key_id"), Str(body, "encrypted_value")) is { } rejected) return rejected;
            bool created;
            lock (sim.Store.Lock)
            {
                created = !r!.Secrets.ContainsKey(name);
                r.Secrets[name] = sim.Now;
                sim.Store.Save();
            }
            return created ? Results.Json(new { }, statusCode: 201) : Results.NoContent();
        });

        api.MapDelete("/repos/{org}/{repo}/actions/secrets/{name}", (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            lock (sim.Store.Lock)
            {
                if (!r!.Secrets.Remove(name)) return NotFound();
                sim.Store.Save();
            }
            return Results.NoContent();
        });

        // ---- repository variables ----------------------------------------------------
        api.MapGet("/repos/{org}/{repo}/actions/variables", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            List<KeyValuePair<string, string>> all;
            lock (sim.Store.Lock) all = r!.Variables.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
            return Results.Json(new
            {
                total_count = all.Count,
                variables = Page(ctx, all).Select(v => new { name = v.Key, value = v.Value, created_at = Iso(r!.CreatedAt), updated_at = Iso(r.UpdatedAt) }).ToArray()
            });
        });

        api.MapPost("/repos/{org}/{repo}/actions/variables", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            var body = await BodyAsync(ctx);
            var name = Str(body, "name"); var value = Str(body, "value");
            if (name is null || value is null || !SecretName.IsMatch(name) || name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
                return Error(422, "Validation Failed", new { resource = "Variable", code = "invalid", field = "name" });
            lock (sim.Store.Lock)
            {
                if (r!.Variables.ContainsKey(name)) return Error(409, "Already exists - Variable already exists");
                r.Variables[name] = value;
                sim.Store.Save();
            }
            return Results.Json(new { }, statusCode: 201);
        });

        api.MapGet("/repos/{org}/{repo}/actions/variables/{name}", (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            lock (sim.Store.Lock)
                return r!.Variables.TryGetValue(name, out var value)
                    ? Results.Json(new { name, value, created_at = Iso(r.CreatedAt), updated_at = Iso(r.UpdatedAt) }) : NotFound();
        });

        api.MapPatch("/repos/{org}/{repo}/actions/variables/{name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            var body = await BodyAsync(ctx);
            lock (sim.Store.Lock)
            {
                if (!r!.Variables.ContainsKey(name)) return NotFound();
                r.Variables[name] = Str(body, "value") ?? r.Variables[name];
                sim.Store.Save();
            }
            return Results.NoContent();
        });

        api.MapDelete("/repos/{org}/{repo}/actions/variables/{name}", (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            lock (sim.Store.Lock)
            {
                if (!r!.Variables.Remove(name)) return NotFound();
                sim.Store.Save();
            }
            return Results.NoContent();
        });

        // ---- environments ------------------------------------------------------------
        api.MapGet("/repos/{org}/{repo}/environments", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            List<GhEnvironment> all; lock (sim.Store.Lock) all = r!.Environments.Values.OrderBy(e => e.Name).ToList();
            return Results.Json(new { total_count = all.Count, environments = Page(ctx, all).Select(e => EnvironmentJson(ctx, r!, e)).ToArray() });
        });

        api.MapPut("/repos/{org}/{repo}/environments/{name}", async (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            var body = await BodyAsync(ctx);
            GhEnvironment env;
            lock (sim.Store.Lock)
            {
                if (!r!.Environments.TryGetValue(name, out env!))
                    r.Environments[name] = env = new GhEnvironment { Name = name, Id = sim.Store.State.NextEnvironmentId++, CreatedAt = sim.Now };
                env.WaitTimer = Int(body, "wait_timer") ?? env.WaitTimer;
                if (body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty("reviewers", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.Array)
                    env.ReviewerCount = rv.GetArrayLength();
                sim.Store.Save();
            }
            return Results.Json(EnvironmentJson(ctx, r!, env));
        });

        api.MapGet("/repos/{org}/{repo}/environments/{name}", (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            lock (sim.Store.Lock)
                return r!.Environments.TryGetValue(name, out var env) ? Results.Json(EnvironmentJson(ctx, r, env)) : NotFound();
        });

        api.MapDelete("/repos/{org}/{repo}/environments/{name}", (HttpContext ctx, string org, string repo, string name) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            if (!CanManageActions(ctx, sim, r!)) return Error(403, "Resource not accessible by integration");
            lock (sim.Store.Lock)
            {
                if (!r!.Environments.Remove(name)) return NotFound();
                sim.Store.Save();
            }
            return Results.NoContent();
        });

        // Environment secrets and variables are addressed by numeric repository id on GitHub.
        api.MapGet("/repositories/{rid:long}/environments/{env}/secrets", (HttpContext ctx, long rid, string env) =>
            WithEnvironment(sim, ctx, rid, env, (r, e) => Results.Json(new
            {
                total_count = e.Secrets.Count,
                secrets = e.Secrets.Select(s => new { name = s.Key, created_at = Iso(s.Value), updated_at = Iso(s.Value) }).ToArray()
            })));
        api.MapGet("/repositories/{rid:long}/environments/{env}/secrets/public-key", (HttpContext ctx, long rid, string env) =>
            WithEnvironment(sim, ctx, rid, env, (r, e) => SecretKey(r)));
        api.MapPut("/repositories/{rid:long}/environments/{env}/secrets/{name}", async (HttpContext ctx, long rid, string env, string name) =>
        {
            var body = await BodyAsync(ctx);
            return WithEnvironment(sim, ctx, rid, env, (r, e) =>
            {
                if (ValidateSecret(r, name, Str(body, "key_id"), Str(body, "encrypted_value")) is { } rejected) return rejected;
                var created = !e.Secrets.ContainsKey(name);
                e.Secrets[name] = sim.Now;
                return created ? Results.Json(new { }, statusCode: 201) : Results.NoContent();
            });
        });
        api.MapGet("/repositories/{rid:long}/environments/{env}/variables", (HttpContext ctx, long rid, string env) =>
            WithEnvironment(sim, ctx, rid, env, (r, e) => Results.Json(new
            {
                total_count = e.Variables.Count,
                variables = e.Variables.Select(v => new { name = v.Key, value = v.Value, created_at = Iso(e.CreatedAt), updated_at = Iso(e.CreatedAt) }).ToArray()
            })));
        api.MapPost("/repositories/{rid:long}/environments/{env}/variables", async (HttpContext ctx, long rid, string env) =>
        {
            var body = await BodyAsync(ctx);
            return WithEnvironment(sim, ctx, rid, env, (r, e) =>
            {
                var name = Str(body, "name"); var value = Str(body, "value");
                if (name is null || value is null || !SecretName.IsMatch(name)) return Error(422, "Validation Failed");
                if (e.Variables.ContainsKey(name)) return Error(409, "Already exists - Variable already exists");
                e.Variables[name] = value;
                return Results.Json(new { }, statusCode: 201);
            });
        });

        // ---- workflows ---------------------------------------------------------------
        api.MapGet("/repos/{org}/{repo}/actions/workflows", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var workflows = await ListWorkflowsAsync(sim, ctx, r!);
            return Results.Json(new { total_count = workflows.Count, workflows = Page(ctx, workflows) });
        });

        api.MapGet("/repos/{org}/{repo}/actions/workflows/{id}", async (HttpContext ctx, string org, string repo, string id) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var workflows = await ListWorkflowsAsync(sim, ctx, r!);
            var match = workflows.FirstOrDefault(w => w["id"]!.ToString() == id || ((string)w["path"]!).EndsWith("/" + id));
            return match is null ? NotFound() : Results.Json(match);
        });
    }

    private static object EnvironmentJson(HttpContext ctx, GhRepo repo, GhEnvironment e) => new Dictionary<string, object?>
    {
        ["id"] = e.Id, ["name"] = e.Name, ["url"] = $"{Api(ctx)}/repos/{repo.FullName}/environments/{e.Name}",
        ["created_at"] = Iso(e.CreatedAt), ["updated_at"] = Iso(e.CreatedAt),
        ["protection_rules"] = e.ReviewerCount > 0 ? new object[] { new { type = "required_reviewers" } } : Array.Empty<object>(),
        ["deployment_branch_policy"] = null
    };

    private static IResult WithEnvironment(GhSim sim, HttpContext ctx, long rid, string env, Func<GhRepo, GhEnvironment, IResult> action)
    {
        GhRepo? r; lock (sim.Store.Lock) r = sim.Store.Repo(rid);
        var who = GhSim.Auth(ctx);
        if (r is null || !sim.CanRead(who, r)) return NotFound();
        if (!CanManageActions(ctx, sim, r)) return Error(403, "Resource not accessible by integration");
        lock (sim.Store.Lock)
        {
            if (!r.Environments.TryGetValue(env, out var e)) return NotFound();
            var result = action(r, e);
            sim.Store.Save();
            return result;
        }
    }

    private static async Task<List<Dictionary<string, object?>>> ListWorkflowsAsync(GhSim sim, HttpContext ctx, GhRepo repo)
    {
        var bare = sim.Store.BarePath(repo);
        var result = new List<Dictionary<string, object?>>();
        var rev = await RepoGit.RevParseAsync(bare, "refs/heads/" + repo.DefaultBranch);
        if (rev is null) return result;
        var files = (await RepoGit.TreeAsync(bare, rev, ".github/workflows", recursive: true))
            .Where(e => e.Type == "blob" && e.Path.StartsWith(".github/workflows/") && e.Path.Count(c => c == '/') == 2 &&
                (e.Path.EndsWith(".yml") || e.Path.EndsWith(".yaml")))
            .OrderBy(e => e.Path, StringComparer.Ordinal);
        var api = $"{Api(ctx)}/repos/{repo.FullName}";
        foreach (var file in files)
        {
            var text = Encoding.UTF8.GetString(await RepoGit.BlobAsync(bare, file.Sha) ?? []);
            var title = Regex.Match(text, @"^name:\s*['""]?(?<n>[^'""\r\n]+?)['""]?\s*$", RegexOptions.Multiline) is { Success: true } m
                ? m.Groups["n"].Value : file.Path;
            var id = Math.Abs(BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(repo.Id + file.Path)), 0)) + 1_000_000L;
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = id, ["node_id"] = NodeId("0:Workflow", id), ["name"] = title, ["path"] = file.Path, ["state"] = "active",
                ["created_at"] = Iso(repo.CreatedAt), ["updated_at"] = Iso(repo.PushedAt),
                ["url"] = $"{api}/actions/workflows/{id}", ["html_url"] = $"{Origin(ctx)}/{repo.FullName}/blob/{repo.DefaultBranch}/{file.Path}",
                ["badge_url"] = $"{Origin(ctx)}/{repo.FullName}/workflows/{title}/badge.svg"
            });
        }
        return result;
    }

    // ---- rulesets -----------------------------------------------------------------------

    private static void MapRules(RouteGroupBuilder api, GhSim sim)
    {
        api.MapGet("/repos/{org}/{repo}/rulesets", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            List<GhRuleset> all; lock (sim.Store.Lock) all = [.. r!.Rulesets];
            return Results.Json(Page(ctx, all).Select(x => GhJson.Ruleset(ctx, r!, x)).ToArray());
        });

        api.MapGet("/repos/{org}/{repo}/rules/branches/{**branch}", (HttpContext ctx, string org, string repo, string branch) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var refName = "refs/heads/" + Uri.UnescapeDataString(branch);
            List<object> rules; 
            lock (sim.Store.Lock)
                rules = GhSim.RulesetsFor(r!, refName).SelectMany(rs => rs.Rules.Select(t => (object)new
                { type = t, ruleset_source_type = "Repository", ruleset_source = r!.FullName, ruleset_id = rs.Id })).ToList();
            return Results.Json(Page(ctx, rules));
        });
    }
}
