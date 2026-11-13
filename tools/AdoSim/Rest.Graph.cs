using System.Text;

namespace AdoSim;

// Azure DevOps Services identity surface: the Graph API (users, groups, memberships) that a Server
// does not have. Graph descriptors differ from the identity descriptors that access control entries use;
// legacyDescriptor links the two, as the migrator expects.
public static partial class AdoSimApp
{
    private static string GraphDescriptor(SimIdentity i) =>
        (i.IsContainer ? "vssgp." : "aad.") + Convert.ToBase64String(Encoding.UTF8.GetBytes(i.Id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void MapServicesGraph(RouteGroupBuilder g, SimModel model, SimOptions options)
    {
        g.MapGet("/_apis/graph/users", (HttpContext ctx) =>
            GraphPage(ctx, options, model.Identities.Where(i => !i.IsContainer).ToList(), i => GraphSubject(ctx, i)));

        g.MapGet("/_apis/graph/groups", (HttpContext ctx) =>
            GraphPage(ctx, options, model.Identities.Where(i => i.IsContainer).ToList(), i => GraphSubject(ctx, i)));

        // direction=down lists the direct members of a group (the migrator never infers transitive ones).
        g.MapGet("/_apis/graph/Memberships/{descriptor}", (HttpContext ctx, string descriptor) =>
        {
            var container = model.Identities.FirstOrDefault(i => i.IsContainer && GraphDescriptor(i) == Uri.UnescapeDataString(descriptor));
            if (container is null) return NotFound();
            var members = container.MemberNames.Select(model.FindByName).OfType<SimIdentity>().ToList();
            return GraphPage(ctx, options, members, m => new
            {
                memberDescriptor = GraphDescriptor(m), containerDescriptor = GraphDescriptor(container)
            });
        });
    }

    private static IResult GraphPage<T>(HttpContext ctx, SimOptions options, IReadOnlyList<T> all, Func<T, object> map)
    {
        var offset = Int(ctx, "continuationToken") ?? 0;
        var page = all.Skip(offset).Take(options.GraphPageSize).Select(map).ToArray();
        if (offset + options.GraphPageSize < all.Count)
            ctx.Response.Headers["X-MS-ContinuationToken"] = (offset + options.GraphPageSize).ToString();
        return Results.Json(new { count = page.Length, value = page });
    }

    private static object GraphSubject(HttpContext ctx, SimIdentity i)
    {
        var descriptor = GraphDescriptor(i);
        var root = $"{ctx.Request.Scheme}://{ctx.Request.Host}/vssps/{ctx.Request.RouteValues["collection"]}";
        var shortName = i.Name.Contains('\\') ? i.Name[(i.Name.LastIndexOf('\\') + 1)..] : i.Name;
        return new
        {
            subjectKind = i.IsContainer ? "group" : "user", descriptor, legacyDescriptor = i.Descriptor,
            displayName = i.IsContainer ? shortName : i.Display, principalName = i.IsContainer ? i.Name : i.Mail ?? i.Name,
            mailAddress = i.Mail, origin = i.IsContainer ? "vsts" : "aad", originId = i.Id,
            url = $"{root}/_apis/graph/{(i.IsContainer ? "groups" : "users")}/{descriptor}"
        };
    }
}
