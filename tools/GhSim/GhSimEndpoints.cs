using System.Text;
using System.Text.Json;

namespace GhSim;

public static partial class GhSimApp
{
    private const string PreReceiveHook = """
        #!/bin/sh
        # Repository rules for pushes: the rule list is rewritten by the simulator (see sim-rules).
        rules="${GIT_DIR:-.}/sim-rules"
        [ -f "$rules" ] || exit 0
        zero=0000000000000000000000000000000000000000
        status=0
        reject() { echo "error: GH013: Repository rule violations found for $1." >&2; echo "- $2" >&2; status=1; }
        while read old new ref; do
          while IFS='	' read kind glob; do
            case "$ref" in $glob) ;; *) continue ;; esac
            case "$kind" in
              creation) [ "$old" = "$zero" ] && reject "$ref" "Cannot create ref due to creations being restricted." ;;
              deletion) [ "$new" = "$zero" ] && reject "$ref" "Cannot delete this ref." ;;
              update) [ "$old" != "$zero" ] && [ "$new" != "$zero" ] && reject "$ref" "Cannot update this ref." ;;
              pull_request) [ "$old" != "$zero" ] && [ "$new" != "$zero" ] && reject "$ref" "Changes must be made through a pull request." ;;
              non_fast_forward)
                if [ "$old" != "$zero" ] && [ "$new" != "$zero" ] && ! git merge-base --is-ancestor "$old" "$new" 2>/dev/null; then
                  reject "$ref" "Cannot update this ref: non-fast-forward updates are blocked."
                fi ;;
            esac
          done < "$rules"
        done
        exit $status
        """;

    // Rules are enforced on pushes by git itself (a pre-receive hook sees the real incoming objects).
    public static async Task WriteRulesAsync(GhSim sim, GhRepo repo)
    {
        var bare = sim.Store.BarePath(repo);
        var lines = new StringBuilder();
        lock (sim.Store.Lock)
            foreach (var ruleset in repo.Rulesets.Where(r => r.Enforcement == "active"))
                foreach (var pattern in ruleset.Include)
                {
                    var prefix = ruleset.Target == "tag" ? "refs/tags/" : "refs/heads/";
                    var glob = pattern switch
                    {
                        "~ALL" => prefix + "*",
                        "~DEFAULT_BRANCH" => "refs/heads/" + repo.DefaultBranch,
                        _ => pattern
                    };
                    foreach (var rule in ruleset.Rules) lines.Append(rule).Append('\t').Append(glob).Append('\n');
                }
        var hooks = Path.Combine(bare, "hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-receive");
        await File.WriteAllTextAsync(hook, PreReceiveHook.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(bare, "sim-rules"), lines.ToString());
    }

    private static void MapSimEndpoints(WebApplication app, GhSim sim)
    {
        var admin = app.MapGroup("/_sim");

        admin.MapGet("/state", async (HttpContext ctx) =>
        {
            List<GhRepo> repos; List<GhOrg> orgs;
            lock (sim.Store.Lock) { repos = [.. sim.Store.State.Repos]; orgs = [.. sim.Store.State.Orgs]; }
            var items = new List<object>();
            foreach (var r in repos.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var bare = sim.Store.BarePath(r);
                var refs = await RepoGit.RefsAsync(bare);
                var workflows = (await ListWorkflowsAsync(sim, ctx, r)).Select(w => (string)w["path"]!).ToArray();
                lock (sim.Store.Lock)
                    items.Add(new
                    {
                        fullName = r.FullName, id = r.Id, @private = r.Private, archived = r.Archived, disabled = r.Disabled,
                        defaultBranch = r.DefaultBranch, empty = refs.Count == 0,
                        refs = refs.ToDictionary(x => x.Name, x => x.ObjectId),
                        workflows, secrets = r.Secrets.Keys.OrderBy(x => x).ToArray(), variables = r.Variables,
                        environments = r.Environments.Keys.OrderBy(x => x).ToArray(),
                        issues = r.Issues.Count, pulls = r.Pulls.Count, rulesets = r.Rulesets.Count
                    });
            }
            return Results.Json(new
            {
                orgs = orgs.Select(o => new { o.Login, members = o.Members }).ToArray(), repositories = items
            });
        });

        // Every request the simulator served: lets tests prove what a client did (e.g. read-only phases).
        admin.MapGet("/requests", (HttpContext ctx) =>
        {
            IEnumerable<RequestLogEntry> all = sim.Requests.ToArray();
            if (Q(ctx, "method") is { Length: > 0 } m) all = all.Where(r => r.Method.Equals(m, StringComparison.OrdinalIgnoreCase));
            if (Q(ctx, "path") is { Length: > 0 } p) all = all.Where(r => r.Path.Contains(p, StringComparison.Ordinal));
            var limit = int.TryParse(Q(ctx, "limit"), out var l) ? Math.Clamp(l, 1, 2000) : 200;
            return Results.Json(all.TakeLast(limit).ToArray());
        });
        admin.MapDelete("/requests", () => { sim.Requests.Clear(); return Results.NoContent(); });

        admin.MapPost("/reset", async () =>
        {
            sim.InstallationTokens.Clear();
            sim.Requests.Clear();
            lock (sim.Faults) sim.Faults.Clear();
            sim.Store.Reset(sim.Options.Seed.Hash());
            await ApplySeedAsync(sim);
            foreach (var repo in sim.Store.State.Repos.ToList()) await WriteRulesAsync(sim, repo);
            return Results.NoContent();
        });

        // Register a GitHub App (id, public key, installations) at runtime, e.g. with a freshly generated key pair.
        admin.MapPost("/apps", async (HttpContext ctx) =>
        {
            var seed = await JsonSerializer.DeserializeAsync<SeedApp>(ctx.Request.Body, GhSeed.Json);
            if (seed is null || seed.Id <= 0) return Error(422, "Validation Failed");
            lock (sim.Store.Lock)
            {
                sim.Store.State.Apps.RemoveAll(a => a.Id == seed.Id);
                sim.Store.State.Apps.Add(ToApp(seed));
                sim.Store.Save();
            }
            return Results.Json(new { id = seed.Id }, statusCode: 201);
        });

        admin.MapPost("/faults", async (HttpContext ctx) =>
        {
            var fault = await JsonSerializer.DeserializeAsync<Fault>(ctx.Request.Body, GhSeed.Json);
            if (fault is null || fault.PathRegex.Length == 0) return Error(422, "Validation Failed");
            lock (sim.Faults) sim.Faults.Add(fault);
            return Results.Json(new { faults = sim.Faults.Count }, statusCode: 201);
        });
        admin.MapDelete("/faults", () => { lock (sim.Faults) sim.Faults.Clear(); return Results.NoContent(); });

        admin.MapPost("/repos/{org}/{repo}/rulesets", async (HttpContext ctx, string org, string repo) =>
        {
            GhRepo? r; lock (sim.Store.Lock) r = sim.Store.Repo(org, repo);
            if (r is null) return NotFound();
            var seed = await JsonSerializer.DeserializeAsync<SeedRuleset>(ctx.Request.Body, GhSeed.Json);
            if (seed is null) return Error(422, "Validation Failed");
            lock (sim.Store.Lock)
            {
                r.Rulesets.Add(new GhRuleset
                {
                    Id = sim.Store.State.NextRulesetId++, Name = seed.Name, Enforcement = seed.Enforcement,
                    Target = seed.Target, Include = seed.Include, Rules = seed.Rules
                });
                sim.Store.Save();
            }
            await WriteRulesAsync(sim, r);
            return Results.Json(new { rulesets = r.Rulesets.Count }, statusCode: 201);
        });
    }
}
