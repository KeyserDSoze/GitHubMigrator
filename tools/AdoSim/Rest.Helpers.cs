using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace AdoSim;

public static partial class AdoSimApp
{
    private static readonly DateTimeOffset DefaultDate = new(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static string? Q(HttpContext ctx, string name) =>
        ctx.Request.Query.TryGetValue(name, out StringValues v) && v.Count > 0 ? v[0] : null;
    private static int? Int(HttpContext ctx, string name) => int.TryParse(Q(ctx, name), out var v) ? v : null;
    private static bool Flag(HttpContext ctx, string name) => bool.TryParse(Q(ctx, name), out var v) && v;
    private static IResult NotFound() => Results.Json(new { message = "Not found" }, statusCode: 404);
    private static IResult Bad(string message) => Results.Json(new { message }, statusCode: 400);

    private static string Iso(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string Esc(string text) => Uri.EscapeDataString(text);

    private static SimProject? Find(SimModel m, string idOrName) =>
        m.Projects.FirstOrDefault(p => p.Id == idOrName || p.Name == Uri.UnescapeDataString(idOrName));

    private static SimRepo? FindRepo(SimModel m, string project, string repo) =>
        Find(m, project)?.Repos.FirstOrDefault(r => r.Id == repo || r.Name == Uri.UnescapeDataString(repo));

    private static (SimProject?, SeedPipeline?) FindPipeline(SimModel m, string project, int id)
    {
        var p = Find(m, project);
        return (p, p?.Spec.Pipelines?.FirstOrDefault(x => x.Id == id));
    }

    // Server: {scheme}://{host}/tfs/{collection}; Services: {scheme}://{host}/{organization}
    private static string Root(HttpContext ctx) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{(ctx.Request.Path.StartsWithSegments("/tfs") ? "/tfs" : "")}/{ctx.Request.RouteValues["collection"]}";

    private static object Link(string href) => new { href };

    // Azure DevOps reports the repository size in bytes (0 for an empty repository).
    private static long RepoSize(SimRepo r)
    {
        var objects = Path.Combine(r.BarePath, "objects");
        return Directory.Exists(objects)
            ? new DirectoryInfo(objects).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    private static object ProjectRef(string root, SimProject p) => new
    {
        id = p.Id, name = p.Name, url = $"{root}/_apis/projects/{p.Id}", state = "wellFormed", visibility = "private"
    };

    private static object RepoRef(string root, SimRepo r) => new
    {
        id = r.Id, name = r.Name, url = $"{root}/{r.ProjectId}/_apis/git/repositories/{r.Id}",
        project = new { id = r.ProjectId, name = r.ProjectName, url = $"{root}/_apis/projects/{r.ProjectId}", state = "wellFormed" },
        remoteUrl = RemoteUrl(root, r), isDisabled = r.Spec.Disabled
    };

    private static string RemoteUrl(string root, SimRepo r) => $"{root}/{Esc(r.ProjectName)}/_git/{Esc(r.Name)}";

    // Users resolve to their identity; unknown or absent names fall back to the simulator administrator.
    private static object IdentityRef(SimModel m, string root, string? name)
    {
        var user = name is null ? null : m.FindByName(name);
        if (user is null)
            return new
            {
                displayName = "Sim Administrator", id = Ids.Guid("auth"), uniqueName = "SIM\\admin",
                url = $"{root}/_apis/Identities/{Ids.Guid("auth")}", imageUrl = $"{root}/_api/_common/identityImage?id={Ids.Guid("auth")}"
            };
        return new
        {
            displayName = user.Display, id = user.Id, uniqueName = user.IsContainer ? user.Name : "SIM\\" + user.Name,
            url = $"{root}/_apis/Identities/{user.Id}", imageUrl = $"{root}/_api/_common/identityImage?id={user.Id}",
            descriptor = user.Descriptor
        };
    }

    private static string FullRef(string branch) => branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private static bool WildcardMatch(string value, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(value,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Offset-based continuation, same convention as the refs endpoint.
    private static IResult Page<T>(HttpContext ctx, IReadOnlyList<T> all, int defaultTop, Func<T, object> shape)
    {
        var top = Math.Max(Int(ctx, "$top") ?? defaultTop, 0);
        var offset = Math.Max(Int(ctx, "continuationToken") ?? 0, 0);
        var page = all.Skip(offset).Take(top).Select(shape).ToArray();
        if (offset + top < all.Count) ctx.Response.Headers["X-MS-ContinuationToken"] = (offset + top).ToString();
        return Results.Json(new { count = page.Length, value = page });
    }
}
