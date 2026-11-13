namespace GhSim;

// JSON shapes follow the GitHub REST documentation (snake_case, explicit nulls) because the
// migrator's readers validate them strictly.
public static class GhJson
{
    public static object Account(GhSim sim, HttpContext ctx, string login, long id, string type) => new Dictionary<string, object?>
    {
        ["login"] = login, ["id"] = id, ["node_id"] = GhSimApp.NodeId(type == "Organization" ? "012:Organization" : "04:User", id),
        ["type"] = type, ["site_admin"] = false, ["url"] = $"{GhSimApp.Api(ctx)}/{(type == "Organization" ? "orgs" : "users")}/{login}",
        ["html_url"] = $"{GhSimApp.Origin(ctx)}/{login}"
    };

    public static object Owner(GhSim sim, HttpContext ctx, GhRepo repo)
    {
        var org = sim.Store.Org(repo.Org)!;
        return Account(sim, ctx, org.Login, org.Id, "Organization");
    }

    public static object Repo(GhSim sim, HttpContext ctx, GhRepo r, Principal who)
    {
        var push = sim.CanWrite(who, r);
        var admin = sim.CanAdmin(who, r);
        var api = GhSimApp.Api(ctx); var web = GhSimApp.Origin(ctx);
        return new Dictionary<string, object?>
        {
            ["id"] = r.Id, ["node_id"] = GhSimApp.NodeId("010:Repository", r.Id), ["name"] = r.Name, ["full_name"] = r.FullName,
            ["private"] = r.Private, ["owner"] = Owner(sim, ctx, r), ["html_url"] = $"{web}/{r.FullName}",
            ["description"] = r.Description, ["fork"] = false, ["url"] = $"{api}/repos/{r.FullName}",
            ["clone_url"] = $"{web}/{r.FullName}.git", ["git_url"] = $"git://{ctx.Request.Host}/{r.FullName}.git",
            ["default_branch"] = r.DefaultBranch, ["archived"] = r.Archived, ["disabled"] = r.Disabled,
            ["visibility"] = r.Private ? "private" : "public",
            ["permissions"] = new { admin, maintain = admin, push, triage = push, pull = true },
            ["created_at"] = GhSimApp.Iso(r.CreatedAt), ["updated_at"] = GhSimApp.Iso(r.UpdatedAt), ["pushed_at"] = GhSimApp.Iso(r.PushedAt),
            ["size"] = 0, ["has_issues"] = true, ["has_projects"] = false, ["has_wiki"] = false, ["is_template"] = false,
            ["open_issues_count"] = r.Issues.Count(i => i.State == "open") + r.Pulls.Count(p => p.State == "open"),
            ["topics"] = Array.Empty<string>()
        };
    }

    public static object GitRef(HttpContext ctx, GhRepo repo, RefInfo r) => new Dictionary<string, object?>
    {
        ["ref"] = r.Name, ["node_id"] = GhSimApp.NodeId("0:Ref", r.Name),
        ["url"] = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}/git/{r.Name}",
        ["object"] = new
        {
            sha = r.ObjectId, type = r.ObjectType,
            url = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}/git/{(r.ObjectType == "tag" ? "tags" : "commits")}/{r.ObjectId}"
        }
    };

    private static object Person(string name, string email, DateTimeOffset date) =>
        new { name, email, date = GhSimApp.Iso(date) };

    public static object Verification() => new { verified = false, reason = "unsigned", signature = (string?)null, payload = (string?)null };

    public static object GitCommit(HttpContext ctx, GhRepo repo, CommitInfo c)
    {
        var api = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}";
        return new Dictionary<string, object?>
        {
            ["sha"] = c.Sha, ["node_id"] = GhSimApp.NodeId("06:Commit", c.Sha), ["url"] = $"{api}/git/commits/{c.Sha}",
            ["html_url"] = $"{GhSimApp.Origin(ctx)}/{repo.FullName}/commit/{c.Sha}",
            ["author"] = Person(c.AuthorName, c.AuthorEmail, c.AuthorDate),
            ["committer"] = Person(c.CommitterName, c.CommitterEmail, c.CommitterDate),
            ["tree"] = new { sha = c.Tree, url = $"{api}/git/trees/{c.Tree}" },
            ["message"] = c.Message.TrimEnd('\n'),
            ["parents"] = c.Parents.Select(p => new { sha = p, url = $"{api}/git/commits/{p}", html_url = $"{GhSimApp.Origin(ctx)}/{repo.FullName}/commit/{p}" }).ToArray(),
            ["verification"] = Verification()
        };
    }

    public static object RestCommit(HttpContext ctx, GhRepo repo, CommitInfo c)
    {
        var api = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}";
        return new Dictionary<string, object?>
        {
            ["sha"] = c.Sha, ["node_id"] = GhSimApp.NodeId("06:Commit", c.Sha), ["url"] = $"{api}/commits/{c.Sha}",
            ["html_url"] = $"{GhSimApp.Origin(ctx)}/{repo.FullName}/commit/{c.Sha}",
            ["commit"] = new
            {
                author = Person(c.AuthorName, c.AuthorEmail, c.AuthorDate),
                committer = Person(c.CommitterName, c.CommitterEmail, c.CommitterDate),
                message = c.Message.TrimEnd('\n'), tree = new { sha = c.Tree, url = $"{api}/git/trees/{c.Tree}" },
                url = $"{api}/git/commits/{c.Sha}", comment_count = 0, verification = Verification()
            },
            ["parents"] = c.Parents.Select(p => new { sha = p, url = $"{api}/commits/{p}" }).ToArray(),
            ["author"] = (object?)null, ["committer"] = (object?)null
        };
    }

    public static object GitTag(HttpContext ctx, GhRepo repo, TagInfo t)
    {
        var api = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}";
        return new Dictionary<string, object?>
        {
            ["node_id"] = GhSimApp.NodeId("03:Tag", t.Sha), ["tag"] = t.Tag, ["sha"] = t.Sha,
            ["url"] = $"{api}/git/tags/{t.Sha}", ["message"] = t.Message.TrimEnd('\n'),
            ["tagger"] = Person(t.TaggerName, t.TaggerEmail, t.TaggerDate),
            ["object"] = new { type = t.TargetType, sha = t.TargetSha, url = $"{api}/git/{t.TargetType}s/{t.TargetSha}" },
            ["verification"] = Verification()
        };
    }

    public static object Issue(HttpContext ctx, GhRepo repo, GhIssue i, GhPull? pull = null)
    {
        var api = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}";
        var d = new Dictionary<string, object?>
        {
            ["id"] = repo.Id * 1000 + i.Number, ["number"] = i.Number, ["title"] = i.Title, ["body"] = i.Body, ["state"] = i.State,
            ["user"] = new { login = i.Author, type = "User" }, ["labels"] = i.Labels.Select(l => new { name = l }).ToArray(),
            ["comments"] = i.Comments.Count, ["created_at"] = GhSimApp.Iso(i.CreatedAt), ["updated_at"] = GhSimApp.Iso(i.UpdatedAt),
            ["closed_at"] = i.ClosedAt is { } c ? GhSimApp.Iso(c) : null,
            ["url"] = $"{api}/issues/{i.Number}", ["html_url"] = $"{GhSimApp.Origin(ctx)}/{repo.FullName}/issues/{i.Number}"
        };
        if (pull is not null) d["pull_request"] = new { url = $"{api}/pulls/{pull.Number}" };
        return d;
    }

    public static object Pull(GhSim sim, HttpContext ctx, GhRepo repo, GhPull p)
    {
        var api = $"{GhSimApp.Api(ctx)}/repos/{repo.FullName}";
        object Side(string reference, string sha) => new
        {
            label = $"{repo.Org}:{reference}", @ref = reference, sha, user = Owner(sim, ctx, repo),
            repo = new { id = repo.Id, name = repo.Name, full_name = repo.FullName }
        };
        return new Dictionary<string, object?>
        {
            ["id"] = repo.Id * 1000 + p.Number, ["number"] = p.Number, ["state"] = p.State, ["draft"] = p.Draft,
            ["title"] = p.Title, ["body"] = p.Body, ["user"] = new { login = p.Author, type = "User" },
            ["head"] = Side(p.HeadRef, p.HeadSha), ["base"] = Side(p.BaseRef, p.BaseSha),
            ["merged"] = p.Merged, ["merge_commit_sha"] = p.MergeCommitSha, ["mergeable"] = true,
            ["created_at"] = GhSimApp.Iso(p.CreatedAt), ["updated_at"] = GhSimApp.Iso(p.UpdatedAt),
            ["closed_at"] = p.ClosedAt is { } c ? GhSimApp.Iso(c) : null, ["merged_at"] = p.Merged && p.ClosedAt is { } m ? GhSimApp.Iso(m) : null,
            ["url"] = $"{api}/pulls/{p.Number}", ["html_url"] = $"{GhSimApp.Origin(ctx)}/{repo.FullName}/pull/{p.Number}"
        };
    }

    public static object Ruleset(HttpContext ctx, GhRepo repo, GhRuleset r) => new Dictionary<string, object?>
    {
        ["id"] = r.Id, ["name"] = r.Name, ["target"] = r.Target, ["source_type"] = "Repository", ["source"] = repo.FullName,
        ["enforcement"] = r.Enforcement,
        ["conditions"] = new { ref_name = new { include = r.Include, exclude = Array.Empty<string>() } },
        ["rules"] = r.Rules.Select(t => new { type = t }).ToArray()
    };
}
