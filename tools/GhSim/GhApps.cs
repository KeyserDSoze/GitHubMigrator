using System.Security.Cryptography;

namespace GhSim;

public static partial class GhSimApp
{
    private static void MapApps(RouteGroupBuilder api, GhSim sim)
    {
        api.MapGet("/app", (HttpContext ctx) =>
        {
            var who = GhSim.Auth(ctx);
            return who.Kind != PrincipalKind.App ? Error(401, "A JSON web token could not be decoded") : Results.Json(new
            {
                id = who.App!.Id, slug = who.App.Slug, name = who.App.Slug, node_id = NodeId("03:App", who.App.Id)
            });
        });

        api.MapGet("/app/installations/{id:long}", (HttpContext ctx, long id) =>
        {
            var who = GhSim.Auth(ctx);
            if (who.Kind != PrincipalKind.App) return Error(401, "A JSON web token could not be decoded");
            GhInstallation? installation; GhOrg? org;
            lock (sim.Store.Lock)
            {
                installation = who.App!.Installations.FirstOrDefault(i => i.Id == id);
                org = installation is null ? null : sim.Store.Org(installation.Org);
            }
            if (installation is null || org is null) return NotFound();
            return Results.Json(new Dictionary<string, object?>
            {
                ["id"] = installation.Id, ["app_id"] = who.App!.Id, ["app_slug"] = who.App.Slug, ["target_id"] = org.Id,
                ["target_type"] = "Organization", ["repository_selection"] = installation.RepositorySelection,
                ["suspended_at"] = null,
                ["account"] = new { login = org.Login, id = org.Id, type = "Organization" },
                ["permissions"] = Granted(installation.Contents, installation.PullRequests, installation.Workflows),
                ["events"] = Array.Empty<string>()
            });
        });

        api.MapPost("/app/installations/{id:long}/access_tokens", async (HttpContext ctx, long id) =>
        {
            var who = GhSim.Auth(ctx);
            if (who.Kind != PrincipalKind.App) return Error(401, "A JSON web token could not be decoded");
            var body = await BodyAsync(ctx);
            var requested = "write"; string? pullRequests = null, workflows = null;
            if (body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty("permissions", out var permissions) &&
                permissions.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var p in permissions.EnumerateObject())
                    if (p.Name is not ("contents" or "metadata" or "pull_requests" or "workflows"))
                        return Error(422, $"The permission {p.Name} is not granted to this installation.");
                requested = Str(permissions, "contents") ?? "read";
                pullRequests = Str(permissions, "pull_requests"); workflows = Str(permissions, "workflows");
            }
            if (requested is not ("read" or "write") || pullRequests is not (null or "read" or "write") || workflows is not (null or "write"))
                return Error(422, "Validation Failed");

            GhInstallation? installation;
            List<GhRepo> repos;
            lock (sim.Store.Lock)
            {
                installation = who.App!.Installations.FirstOrDefault(i => i.Id == id);
                if (installation is null) return NotFound();
                if (requested == "write" && installation.Contents != "write")
                    return Error(422, "The permission contents is not granted to this installation.");
                if (pullRequests is not null && (installation.PullRequests == "none" || (pullRequests == "write" && installation.PullRequests != "write")))
                    return Error(422, "The permission pull_requests is not granted to this installation.");
                if (workflows is not null && installation.Workflows != "write")
                    return Error(422, "The permission workflows is not granted to this installation.");
                var inOrg = sim.Store.State.Repos.Where(r => r.Org.Equals(installation.Org, StringComparison.OrdinalIgnoreCase)).ToList();
                if (body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty("repositories", out var names) &&
                    names.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var wanted = names.EnumerateArray().Select(n => n.GetString() ?? "").ToList();
                    repos = inOrg.Where(r => wanted.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (repos.Count != wanted.Count)
                        return Error(422, "There is at least one repository that does not exist or is not accessible to the parent installation.");
                }
                else repos = installation.RepositorySelection == "all" ? inOrg : [];
            }

            var token = "ghs_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(27)).Replace('+', 'A').Replace('/', 'B').TrimEnd('=');
            var expires = sim.Now.AddMinutes(60);
            sim.InstallationTokens[token] = new InstallationToken
            {
                Token = token, App = who.App!, Installation = installation, Contents = requested,
                PullRequests = pullRequests ?? "none", Workflows = workflows ?? "none", RepositoryIds = repos.Select(r => r.Id).ToHashSet(), ExpiresAt = expires
            };
            return Results.Json(new Dictionary<string, object?>
            {
                ["token"] = token, ["expires_at"] = Iso(expires),
                ["permissions"] = Granted(requested, pullRequests ?? "none", workflows ?? "none"),
                ["repository_selection"] = repos.Count == 0 ? installation.RepositorySelection : "selected",
                ["repositories"] = repos.Select(r => new
                {
                    id = r.Id, node_id = NodeId("010:Repository", r.Id), name = r.Name, full_name = r.FullName, @private = r.Private,
                    owner = GhJson.Owner(sim, ctx, r)
                }).ToArray()
            }, statusCode: 201);
        });
    }

    // Only what is granted is listed, as on GitHub; metadata is always read.
    private static Dictionary<string, string> Granted(string contents, string pullRequests, string workflows)
    {
        var granted = new Dictionary<string, string> { ["contents"] = contents, ["metadata"] = "read" };
        if (pullRequests != "none") granted["pull_requests"] = pullRequests;
        if (workflows != "none") granted["workflows"] = workflows;
        return granted;
    }
}
