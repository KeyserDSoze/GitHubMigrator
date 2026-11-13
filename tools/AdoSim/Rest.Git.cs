using System.Text;

namespace AdoSim;

public static partial class AdoSimApp
{
    private static void MapGitApi(RouteGroupBuilder c, SimModel model)
    {
        MapItems(c, model);
        MapCommits(c, model);
        MapPullRequests(c, model);
    }

    // ---- items -------------------------------------------------------------------------------------

    private static object GitItem(string root, SimRepo repo, string commit, string type, string objectId, string path) => new
    {
        objectId, gitObjectType = type, commitId = commit, path, isFolder = type == "tree",
        url = $"{root}/{repo.ProjectId}/_apis/git/repositories/{repo.Id}/items?path={Esc(path)}" +
              $"&versionDescriptor.versionType=commit&versionDescriptor.version={commit}"
    };

    private static void MapItems(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/{project}/_apis/git/repositories/{repo}/items", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var recursion = Q(ctx, "recursionLevel")?.ToLowerInvariant();
            var scope = Q(ctx, "path") ?? Q(ctx, "scopePath") ?? (recursion is null ? null : "/");
            if (scope is null) return NotFound();

            var rev = GitQueries.VersionRev(r, Q(ctx, "versionDescriptor.version"), Q(ctx, "versionDescriptor.versionType"));
            var commit = await GitQueries.CommitOfAsync(r, rev);
            if (commit is null) return NotFound();
            var clean = scope.Replace('\\', '/').Trim('/');
            var display = "/" + clean;
            var root = Root(ctx);

            string type, objectId;
            if (clean.Length == 0) { type = "tree"; objectId = (await GitQueries.TreeOfAsync(r, commit))!; }
            else
            {
                var entry = (await GitQueries.LsTreeAsync(r, commit, clean, children: false, recursive: false)).FirstOrDefault(e => e.Path == clean);
                if (entry is null) return NotFound();
                (type, objectId) = (entry.Type == "tree" ? "tree" : "blob", entry.ObjectId);
            }

            var format = Q(ctx, "$format")?.ToLowerInvariant();
            if (type == "tree")
            {
                if (Flag(ctx, "download") || format is "text" or "octetstream") return Bad("The path is a folder; content formats apply to files only.");
                var items = new List<object> { GitItem(root, r, commit, "tree", objectId, display) };
                if (recursion is "onelevel" or "onelevelplusnestedemptyfolders" or "full")
                    foreach (var e in (await GitQueries.LsTreeAsync(r, commit, clean, children: true, recursive: recursion == "full")).Where(e => e.Path != clean))
                        items.Add(GitItem(root, r, commit, e.Type == "tree" ? "tree" : "blob", e.ObjectId, "/" + e.Path));
                return recursion is null or "none" ? Results.Json(items[0]) : Results.Json(new { count = items.Count, value = items });
            }

            var bytes = await GitQueries.BlobAsync(r, objectId);
            if (bytes is null) return NotFound();
            var fileName = clean[(clean.LastIndexOf('/') + 1)..];
            if (Flag(ctx, "download") || format == "octetstream") return Results.File(bytes, "application/octet-stream", fileName);
            if (format == "text") return Results.File(bytes, "text/plain; charset=utf-8");
            if (format is not (null or "json")) return Bad($"Unsupported $format '{format}'.");

            var binary = GitQueries.IsBinary(bytes);
            var item = new Dictionary<string, object?>
            {
                ["objectId"] = objectId, ["gitObjectType"] = "blob", ["commitId"] = commit, ["path"] = display, ["isFolder"] = false,
                ["url"] = $"{root}/{r.ProjectId}/_apis/git/repositories/{r.Id}/items?path={Esc(display)}" +
                          $"&versionDescriptor.versionType=commit&versionDescriptor.version={commit}",
                ["contentMetadata"] = new
                {
                    fileName, extension = Path.GetExtension(fileName), encoding = 65001,
                    contentType = binary ? "application/octet-stream" : "text/plain"
                }
            };
            // The migrator asks for $format=json&includeContent=true; callers that say nothing keep getting the content.
            if (!binary && (Q(ctx, "includeContent") is null || Flag(ctx, "includeContent")))
                item["content"] = Encoding.UTF8.GetString(bytes);
            return Results.Json(item);
        });
    }

    // ---- commits and branch statistics --------------------------------------------------------------

    private static object Signature(string name, string email, DateTimeOffset date) => new { name, email, date = Iso(date) };

    private static object CommitJson(string root, SimRepo repo, CommitInfo commit, bool detail)
    {
        var comment = commit.Message.Length > 255 ? commit.Message[..255] : commit.Message;
        var url = $"{root}/{repo.ProjectId}/_apis/git/repositories/{repo.Id}/commits/{commit.Id}";
        var json = new Dictionary<string, object?>
        {
            ["commitId"] = commit.Id, ["author"] = Signature(commit.AuthorName, commit.AuthorEmail, commit.AuthorDate),
            ["committer"] = Signature(commit.CommitterName, commit.CommitterEmail, commit.CommitterDate),
            ["comment"] = comment, ["url"] = url, ["remoteUrl"] = $"{RemoteUrl(root, repo)}/commit/{commit.Id}"
        };
        if (comment.Length < commit.Message.Length) json["commentTruncated"] = true;
        if (detail)
        {
            json["treeId"] = commit.Tree;
            json["parents"] = commit.Parents;
        }
        return json;
    }

    private static DateTimeOffset? Date(HttpContext ctx, string name) =>
        DateTimeOffset.TryParse(Q(ctx, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static void MapCommits(RouteGroupBuilder c, SimModel model)
    {
        c.MapGet("/{project}/_apis/git/repositories/{repo}/commits", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var rev = GitQueries.VersionRev(r, Q(ctx, "searchCriteria.itemVersion.version"), Q(ctx, "searchCriteria.itemVersion.versionType"));
            var top = Int(ctx, "searchCriteria.$top") ?? Int(ctx, "$top") ?? 100;
            var skip = Int(ctx, "searchCriteria.$skip") ?? Int(ctx, "$skip") ?? 0;
            var commits = rev is null || rev.StartsWith('-') ? [] : await GitQueries.LogAsync(r, rev, top, skip,
                Q(ctx, "searchCriteria.itemPath"), Q(ctx, "searchCriteria.author"),
                Date(ctx, "searchCriteria.fromDate"), Date(ctx, "searchCriteria.toDate"));
            var root = Root(ctx);
            var value = commits.Select(x => CommitJson(root, r, x, false)).ToArray();
            return Results.Json(new { count = value.Length, value });
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/commits/{commitId}", async (HttpContext ctx, string project, string repo, string commitId) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var commit = await GitQueries.CommitAsync(r, commitId);
            return commit is null ? NotFound() : Results.Json(CommitJson(Root(ctx), r, commit, true));
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/stats/branches", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var baseRev = GitQueries.VersionRev(r, Q(ctx, "baseVersionDescriptor.version"), Q(ctx, "baseVersionDescriptor.versionType"));
            var baseCommit = await GitQueries.CommitOfAsync(r, baseRev);
            if (baseCommit is null) return NotFound();
            var baseName = Q(ctx, "baseVersionDescriptor.version") ?? r.Spec.DefaultBranch;
            var only = Q(ctx, "name");
            var root = Root(ctx);
            var stats = (await GitQueries.BranchStatsAsync(r, baseCommit)).Where(s => only is null || s.Branch == only).ToList();
            var value = new List<object>();
            foreach (var (branch, tip, ahead, behind) in stats.OrderBy(s => s.Branch, StringComparer.Ordinal))
            {
                var info = await GitQueries.CommitAsync(r, tip);
                value.Add(new
                {
                    commit = info is null ? null : CommitJson(root, r, info, false), name = branch, aheadCount = ahead,
                    behindCount = behind, isBaseVersion = branch == baseName
                });
            }
            return Results.Json(new { count = value.Count, value });
        });
    }

    // ---- pull requests -------------------------------------------------------------------------------

    private static async Task<object> PullRequest(SimModel model, string root, SimRepo repo, SeedPullRequest pr)
    {
        var source = FullRef(pr.Source);
        var target = FullRef(pr.Target);
        var sourceCommit = await Git.RevParseAsync(repo, source + "^{commit}");
        var targetCommit = await Git.RevParseAsync(repo, target + "^{commit}");
        var url = $"{root}/{repo.ProjectId}/_apis/git/repositories/{repo.Id}/pullRequests/{pr.Id}";
        object? Commit(string? id) => id is null ? null : new { commitId = id, url = $"{root}/{repo.ProjectId}/_apis/git/repositories/{repo.Id}/commits/{id}" };
        var reviewers = (pr.Reviewers ?? []).Select(rv =>
        {
            var who = model.FindByName(rv.User);
            return new
            {
                reviewerUrl = $"{url}/reviewers/{who?.Id}", vote = rv.Vote, hasDeclined = false, isRequired = rv.Required ? true : (bool?)null,
                isFlagged = false, displayName = who?.Display ?? rv.User, url = $"{root}/_apis/Identities/{who?.Id}", id = who?.Id,
                uniqueName = who is null ? rv.User : who.IsContainer ? who.Name : "SIM\\" + who.Name, isContainer = who?.IsContainer == true
            };
        }).ToArray();
        var json = new Dictionary<string, object?>
        {
            ["repository"] = RepoRef(root, repo), ["pullRequestId"] = pr.Id, ["codeReviewId"] = pr.Id, ["status"] = pr.Status,
            ["createdBy"] = IdentityRef(model, root, pr.CreatedBy), ["creationDate"] = Iso(pr.Created), ["title"] = pr.Title,
            ["description"] = pr.Description, ["sourceRefName"] = source, ["targetRefName"] = target, ["mergeStatus"] = "succeeded",
            ["isDraft"] = pr.Draft, ["mergeId"] = Ids.Guid($"merge:{pr.Id}"), ["lastMergeSourceCommit"] = Commit(sourceCommit),
            ["lastMergeTargetCommit"] = Commit(targetCommit), ["reviewers"] = reviewers,
            ["labels"] = (pr.Labels ?? []).Select(l => new { id = Ids.Guid("label:" + l), name = l, active = true }).ToArray(),
            ["url"] = url, ["supportsIterations"] = true,
            ["artifactId"] = $"vstfs:///Git/PullRequestId/{repo.ProjectId}%2f{repo.Id}%2f{pr.Id}"
        };
        if (pr.Status != "active")
        {
            json["closedDate"] = Iso(pr.Closed ?? pr.Created.AddDays(1));
            json["closedBy"] = IdentityRef(model, root, pr.ClosedBy ?? pr.CreatedBy);
        }
        if (pr.Status == "completed") json["completionOptions"] = new { mergeStrategy = "squash", deleteSourceBranch = false };
        return json;
    }

    private static void MapPullRequests(RouteGroupBuilder c, SimModel model)
    {
        IEnumerable<SeedPullRequest> InRepo(SimRepo r) =>
            (model.Seed.PullRequests ?? []).Where(x => x.Project == r.ProjectName && x.Repo == r.Name).OrderBy(x => x.Id);

        c.MapGet("/{project}/_apis/git/repositories/{repo}/pullrequests", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            var status = (Q(ctx, "searchCriteria.status") ?? "active").ToLowerInvariant();
            if (status is not ("active" or "abandoned" or "completed" or "all" or "notset")) return Bad($"Unknown status '{status}'.");
            var all = InRepo(r).Where(x => status is "all" or "notset" || x.Status == status);
            if (Q(ctx, "searchCriteria.sourceRefName") is { Length: > 0 } source) all = all.Where(x => FullRef(x.Source) == source);
            if (Q(ctx, "searchCriteria.targetRefName") is { Length: > 0 } target) all = all.Where(x => FullRef(x.Target) == target);
            var top = Int(ctx, "$top") ?? 100;
            var skip = Int(ctx, "$skip") ?? 0;
            var root = Root(ctx);
            var value = new List<object>();
            foreach (var pr in all.Skip(skip).Take(top)) value.Add(await PullRequest(model, root, r, pr));
            return Results.Json(new { count = value.Count, value });
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/pullrequests/{id:int}", async (HttpContext ctx, string project, string repo, int id) =>
        {
            var r = FindRepo(model, project, repo);
            var pr = r is null ? null : InRepo(r).FirstOrDefault(x => x.Id == id);
            return pr is null ? NotFound() : Results.Json(await PullRequest(model, Root(ctx), r!, pr));
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/pullrequests/{id:int}/threads", (string project, string repo, int id) =>
        {
            var r = FindRepo(model, project, repo);
            return r is null || InRepo(r).All(x => x.Id != id) ? NotFound() : Results.Json(new { count = 0, value = Array.Empty<object>() });
        });

        c.MapGet("/{project}/_apis/git/repositories/{repo}/pullrequests/{id:int}/workitems", (HttpContext ctx, string project, string repo, int id) =>
        {
            var r = FindRepo(model, project, repo);
            var pr = r is null ? null : InRepo(r).FirstOrDefault(x => x.Id == id);
            if (pr is null) return NotFound();
            var root = Root(ctx);
            var value = (pr.WorkItems ?? []).Select(w => new { id = w.ToString(), url = $"{root}/_apis/wit/workItems/{w}" }).ToArray();
            return Results.Json(new { count = value.Length, value });
        });
    }
}
