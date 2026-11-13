using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

namespace GhSim;

public sealed record GhSimOptions(
    GhSeed Seed, string DataDir, string Urls = "http://0.0.0.0:8080",
    TimeProvider? Clock = null, long MaxPushBytes = 2L * 1024 * 1024 * 1024);

// A GitHub Enterprise Server-shaped simulator: REST under /api/v3, git smart HTTP under /{org}/{repo}.git.
// Repositories are real bare git repositories, so downstream tests exercise real refs, objects and SHAs.
public static partial class GhSimApp
{
    public static async Task<WebApplication> StartAsync(GhSimOptions options)
    {
        Directory.CreateDirectory(options.DataDir);
        var store = new GhStore(options.DataDir);
        var sim = new GhSim(options, store);
        await InitializeAsync(sim);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = null;
            o.SerializerOptions.DictionaryKeyPolicy = null;
        });
        var app = builder.Build();
        app.Use((ctx, next) => Gate(ctx, next, sim));
        app.MapGet("/healthz", () => Results.Text("ok"));
        MapSimEndpoints(app, sim);
        MapRest(app.MapGroup("/api/v3"), sim);
        MapSmartHttp(app, sim);
        await app.StartAsync();
        return app;
    }

    // Seed is applied once; a persisted volume is reused while the seed hash is unchanged.
    public static async Task InitializeAsync(GhSim sim)
    {
        var hash = sim.Options.Seed.Hash();
        if (sim.Store.TryLoad(hash)) return;
        sim.Store.Reset(hash);
        await ApplySeedAsync(sim);
    }

    private static async Task ApplySeedAsync(GhSim sim)
    {
        var seed = sim.Options.Seed;
        lock (sim.Store.Lock)
        {
            var state = sim.Store.State;
            foreach (var u in seed.Users ?? [])
                state.Users.Add(new GhUser { Login = u.Login, Id = state.NextAccountId++, Token = u.Token, ReadOnly = u.ReadOnly, WorkflowScope = u.WorkflowScope });
            foreach (var o in seed.Orgs ?? [])
            {
                var org = new GhOrg { Login = o.Login, Id = state.NextAccountId++, MembersCanCreatePrivateRepositories = o.MembersCanCreatePrivateRepositories };
                foreach (var (login, role) in o.Members ?? []) org.Members[login] = role;
                state.Orgs.Add(org);
            }
            foreach (var a in seed.Apps ?? [])
                state.Apps.Add(ToApp(a));
        }
        foreach (var r in seed.Repos ?? [])
        {
            var repo = await CreateRepositoryAsync(sim, r.Org, r.Name, r.Private, r.Description, r.DefaultBranch, r.Files, seeded: true);
            lock (sim.Store.Lock)
            {
                repo.Archived = r.Archived; repo.Disabled = r.Disabled;
                foreach (var rule in r.Rulesets ?? [])
                    repo.Rulesets.Add(new GhRuleset
                    {
                        Id = sim.Store.State.NextRulesetId++, Name = rule.Name, Enforcement = rule.Enforcement,
                        Target = rule.Target, Include = rule.Include, Rules = rule.Rules
                    });
                foreach (var (name, value) in r.Variables ?? []) repo.Variables[name] = value;
                foreach (var name in r.Secrets ?? []) repo.Secrets[name] = sim.Now;
            }
            await WriteRulesAsync(sim, repo);
        }
        lock (sim.Store.Lock) sim.Store.Save();
    }

    public static GhApp ToApp(SeedApp a) => new()
    {
        Id = a.Id, Slug = a.Slug, PublicKeyPem = a.PublicKeyPem,
        Installations = (a.Installations ?? []).Select(i => new GhInstallation
        { Id = i.Id, Org = i.Org, RepositorySelection = i.RepositorySelection, Contents = i.Contents, PullRequests = i.PullRequests, Workflows = i.Workflows }).ToList()
    };

    public static readonly (string Name, string Email) SeedIdentity = ("Sim Seed", "seed@example.test");
    private static readonly DateTimeOffset SeedDate = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public static async Task<GhRepo> CreateRepositoryAsync(GhSim sim, string org, string name, bool isPrivate,
        string? description, string defaultBranch, Dictionary<string, string>? files, bool seeded)
    {
        GhRepo repo;
        lock (sim.Store.Lock)
        {
            var now = sim.Now;
            var id = sim.Store.State.NextRepoId++;
            repo = new GhRepo
            {
                Id = id, Org = org, Name = name, Private = isPrivate, DefaultBranch = defaultBranch,
                Description = description, CreatedAt = seeded ? SeedDate : now, UpdatedAt = seeded ? SeedDate : now,
                PushedAt = seeded ? SeedDate : now,
                SecretKeyId = (100000000000000000 + id).ToString(),
                SecretKey = GhStore.Deterministic("secret-key:" + id, 32)
            };
            sim.Store.State.Repos.Add(repo);
        }
        var bare = sim.Store.BarePath(repo);
        await RepoGit.InitAsync(bare, defaultBranch);
        await WriteRulesAsync(sim, repo);
        if (files is { Count: > 0 })
        {
            var edits = files.Select(f => (f.Key, (byte[]?)Encoding.UTF8.GetBytes(f.Value))).ToList();
            var date = seeded ? SeedDate : sim.Now;
            await RepoGit.CommitFilesAsync(bare, defaultBranch, edits, "Initial commit", SeedIdentity, SeedIdentity, date);
        }
        lock (sim.Store.Lock) sim.Store.Save();
        return repo;
    }

    // ---- middleware ----------------------------------------------------------------------

    private static async Task Gate(HttpContext ctx, RequestDelegate next, GhSim sim)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path == "/healthz") { await next(ctx); return; }
        var who = sim.Authenticate(ctx);
        ctx.Items[GhSim.PrincipalKey] = who;
        ctx.Response.Headers["X-GitHub-Request-Id"] = Guid.NewGuid().ToString("N");
        ctx.Response.Headers["X-RateLimit-Limit"] = "5000";
        ctx.Response.Headers["X-RateLimit-Remaining"] = "4999";
        ctx.Response.Headers["X-RateLimit-Reset"] = sim.Now.AddHours(1).ToUnixTimeSeconds().ToString();

        var fault = TakeFault(sim, ctx);
        if (fault is not null)
        {
            ctx.Response.StatusCode = fault.RateLimited ? 403 : fault.Status;
            if (fault.RateLimited) ctx.Response.Headers["X-RateLimit-Remaining"] = "0";
            await ctx.Response.WriteAsJsonAsync(new { message = fault.Message ?? "Injected fault" });
            sim.Log(ctx, ctx.Response.StatusCode);
            return;
        }

        var isRest = path.StartsWith("/api/v3", StringComparison.Ordinal) || path.StartsWith("/_sim", StringComparison.Ordinal);
        if (isRest && who.Kind == PrincipalKind.Anonymous)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new
            {
                message = ctx.Request.Headers.ContainsKey("Authorization") ? "Bad credentials" : "Requires authentication",
                documentation_url = "https://docs.github.com/rest"
            });
            sim.Log(ctx, 401);
            return;
        }
        await next(ctx);
        sim.Log(ctx, ctx.Response.StatusCode);
    }

    private static Fault? TakeFault(GhSim sim, HttpContext ctx)
    {
        lock (sim.Faults)
        {
            foreach (var f in sim.Faults)
            {
                if (f.Remaining <= 0) continue;
                if (f.Method is not null && !f.Method.Equals(ctx.Request.Method, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Regex.IsMatch(ctx.Request.Path.Value ?? "", f.PathRegex)) continue;
                if (f.Remaining != int.MaxValue) f.Remaining--;
                return f;
            }
        }
        return null;
    }

    // ---- shared helpers ------------------------------------------------------------------

    public static string Origin(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    public static string Api(HttpContext ctx) => Origin(ctx) + "/api/v3";

    public static IResult Error(int status, string message, params object[] errors)
    {
        var body = new Dictionary<string, object?> { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest" };
        if (errors.Length > 0) body["errors"] = errors;
        return Results.Json(body, statusCode: status);
    }

    public static IResult NotFound() => Error(404, "Not Found");
    public static IResult Invalid(string field, string message, string code = "custom") =>
        Error(422, "Validation Failed", new { resource = "Repository", code, field, message });

    public static async Task<JsonElement> BodyAsync(HttpContext ctx)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(ctx.Request.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    public static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool? Bool(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
    public static int? Int(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static string Q(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count > 0 ? v[0]! : "";

    // GitHub-style page/per_page with Link headers so clients exercise their pagination code.
    public static IReadOnlyList<T> Page<T>(HttpContext ctx, IReadOnlyList<T> all)
    {
        var perPage = int.TryParse(Q(ctx, "per_page"), out var pp) ? Math.Clamp(pp, 1, 100) : 30;
        var page = int.TryParse(Q(ctx, "page"), out var p) ? Math.Max(p, 1) : 1;
        var pages = Math.Max(1, (all.Count + perPage - 1) / perPage);
        var path = Api(ctx) + ctx.Request.Path.Value!["/api/v3".Length..];
        var links = new List<string>();
        if (page < pages)
        {
            links.Add($"<{path}?per_page={perPage}&page={page + 1}>; rel=\"next\"");
            links.Add($"<{path}?per_page={perPage}&page={pages}>; rel=\"last\"");
        }
        if (links.Count > 0) ctx.Response.Headers["Link"] = string.Join(", ", links);
        return all.Skip((page - 1) * perPage).Take(perPage).ToList();
    }

    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    public static string NodeId(string kind, long id) => NodeId(kind, id.ToString());
    public static string NodeId(string kind, string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{kind}{value}"));
}
