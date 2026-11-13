using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmSim;

public static partial class LlmSimApp
{
    private static string Q(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count > 0 ? v[0]! : "";

    // Replies and recipes are echoed without their defaults; the surrounding envelope keeps its zeros.
    private static System.Text.Json.Nodes.JsonNode? Echo<T>(T value) => JsonSerializer.SerializeToNode(value, LlmJson.Echo);

    private static (T? Value, IResult? Error) ReadBody<T>(HttpContext ctx) where T : class
    {
        var bytes = Rec(ctx).Bytes;
        if (bytes.Length == 0) return (null, SimError(400, "A JSON body is required."));
        try { return (JsonSerializer.Deserialize<T>(bytes, LlmJson.Strict), null); }
        catch (JsonException ex) { return (null, SimError(400, "Invalid JSON body: " + ex.Message)); }
    }

    private static void MapSimEndpoints(WebApplication app, LlmState sim)
    {
        var admin = app.MapGroup("/_sim");

        // ---- script queue ----
        admin.MapGet("/script", () =>
        {
            var queued = sim.QueueSnapshot();
            return Results.Json(new
            {
                remaining = queued.Count,
                responses = queued.Select(q => new { reply = Echo(q.Reply), remaining = q.Remaining })
            }, LlmJson.Web);
        });
        admin.MapPost("/script", (HttpContext ctx) =>
        {
            var (body, bad) = ReadBody<ScriptRequest>(ctx);
            if (bad is not null) return bad;
            if (body?.Responses is not { Count: > 0 } responses) return SimError(400, "'responses' must be a non-empty array of replies.");
            for (var i = 0; i < responses.Count; i++)
                if (ReplyRules.Validate(responses[i], sim.Content) is { } problem) return SimError(400, $"responses[{i}]: {problem}");
            var added = sim.Enqueue(responses);
            return Results.Json(new { queued = added, remaining = sim.QueueSnapshot().Count }, LlmJson.Web, statusCode: 201);
        });
        admin.MapDelete("/script", () => { sim.ClearQueue(); return Results.NoContent(); });

        // ---- recipes ----
        admin.MapGet("/recipes", () => Results.Json(
            sim.RecipeSnapshot().Select(r => new { name = r.Recipe.Name, source = r.Source, used = r.Used, when = Echo(r.Recipe.When), reply = Echo(r.Recipe.Reply) }),
            LlmJson.Web));
        admin.MapPost("/recipes", (HttpContext ctx) =>
        {
            var bytes = Rec(ctx).Bytes;
            List<Recipe>? recipes;
            try
            {
                using var document = JsonDocument.Parse(bytes.Length == 0 ? "null"u8.ToArray() : bytes);
                recipes = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.Deserialize<List<Recipe>>(LlmJson.Strict),
                    JsonValueKind.Object => [document.RootElement.Deserialize<Recipe>(LlmJson.Strict)!],
                    _ => null
                };
            }
            catch (JsonException ex) { return SimError(400, "Invalid JSON body: " + ex.Message); }
            if (recipes is not { Count: > 0 }) return SimError(400, "Send one recipe or a non-empty array of recipes.");
            var accepted = new List<(Recipe, Regex?)>();
            foreach (var recipe in recipes)
            {
                if (ReplyRules.Validate(recipe, sim.Content, out var regex) is { } problem) return SimError(400, problem);
                accepted.Add((recipe, regex));
            }
            sim.AddRecipes(accepted);
            return Results.Json(new { added = accepted.Count, total = sim.RecipeSnapshot().Count }, LlmJson.Web, statusCode: 201);
        });
        admin.MapDelete("/recipes", () => { sim.ClearRuntimeRecipes(); return Results.NoContent(); });

        // ---- request log ----
        // Every request to the model surface is logged (also rejected ones); /_sim and /healthz calls are not.
        admin.MapGet("/requests", (HttpContext ctx) =>
        {
            IEnumerable<RequestLogEntry> all = sim.LogSnapshot();
            if (Q(ctx, "path") is { Length: > 0 } path) all = all.Where(r => r.Path.Contains(path, StringComparison.Ordinal));
            if (Q(ctx, "matched") is { Length: > 0 } matched)
                all = all.Where(r => r.Matched == matched || r.Matched.StartsWith(matched + ":", StringComparison.Ordinal));
            var limit = int.TryParse(Q(ctx, "limit"), out var l) ? Math.Clamp(l, 1, LlmState.MaxLogEntries) : 200;
            return Results.Json(all.TakeLast(limit).ToArray(), LlmJson.Web);
        });
        admin.MapDelete("/requests", () => { sim.ClearLog(); return Results.NoContent(); });
        admin.MapGet("/stats", () => Results.Json(sim.StatsSnapshot(), LlmJson.Web));

        // ---- faults: a status for every request whose path matches, before the model surface is reached ----
        admin.MapPost("/faults", (HttpContext ctx) =>
        {
            var (fault, bad) = ReadBody<Fault>(ctx);
            if (bad is not null) return bad;
            if (fault is null || !ReplyRules.Statuses.Contains(fault.Status))
                return SimError(400, $"'status' must be one of: {string.Join(", ", ReplyRules.Statuses.Order())}.");
            if (fault.PathRegex is { } pattern)
            {
                try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
                catch (ArgumentException ex) { return SimError(400, "Invalid pathRegex: " + ex.Message); }
            }
            return Results.Json(new { faults = sim.AddFault(fault) }, LlmJson.Web, statusCode: 201);
        });
        admin.MapDelete("/faults", () => { sim.ClearFaults(); return Results.NoContent(); });

        admin.MapPost("/reset", () => { sim.Reset(); return Results.NoContent(); });
    }
}
