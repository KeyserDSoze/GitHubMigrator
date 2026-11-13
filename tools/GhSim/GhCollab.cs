namespace GhSim;

public static partial class GhSimApp
{
    private static void MapCollaboration(RouteGroupBuilder api, GhSim sim)
    {
        // ---- pull requests -----------------------------------------------------------
        api.MapPost("/repos/{org}/{repo}/pulls", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            var title = Str(body, "title"); var head = Str(body, "head"); var baseRef = Str(body, "base");
            if (string.IsNullOrWhiteSpace(title) || head is null || baseRef is null)
                return Error(422, "Validation Failed", new { resource = "PullRequest", code = "missing_field" });
            head = head.Contains(':') ? head[(head.IndexOf(':') + 1)..] : head;
            var bare = sim.Store.BarePath(r!);
            var headSha = await RepoGit.RevParseAsync(bare, "refs/heads/" + head);
            var baseSha = await RepoGit.RevParseAsync(bare, "refs/heads/" + baseRef);
            if (headSha is null) return Error(422, "Validation Failed", new { resource = "PullRequest", code = "invalid", field = "head" });
            if (baseSha is null) return Error(422, "Validation Failed", new { resource = "PullRequest", code = "invalid", field = "base" });
            var ahead = int.Parse(await GitCli.MustAsync(bare, null, "rev-list", "--count", $"{baseSha}..{headSha}"));
            if (ahead == 0)
                return Error(422, "Validation Failed", new { resource = "PullRequest", code = "custom", message = $"No commits between {baseRef} and {head}" });
            GhPull pull;
            lock (sim.Store.Lock)
            {
                if (r!.Pulls.Any(p => p.State == "open" && p.HeadRef == head && p.BaseRef == baseRef))
                    return Error(422, "Validation Failed", new { resource = "PullRequest", code = "custom", message = $"A pull request already exists for {r.Org}:{head}." });
                var now = sim.Now;
                pull = new GhPull
                {
                    Number = r.NextNumber++, Title = title, Body = Str(body, "body"), Draft = Bool(body, "draft") ?? false,
                    HeadRef = head, HeadSha = headSha, BaseRef = baseRef, BaseSha = baseSha,
                    Author = GhSim.Auth(ctx).Name, CreatedAt = now, UpdatedAt = now
                };
                r.Pulls.Add(pull);
                sim.Store.Save();
            }
            return Results.Json(GhJson.Pull(sim, ctx, r!, pull), statusCode: 201);
        });

        api.MapGet("/repos/{org}/{repo}/pulls", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var state = Q(ctx, "state") is { Length: > 0 } s ? s : "open";
            // head=owner:branch and base=branch narrow the list, as on GitHub (the simulator has no forks: the owner is the repository's)
            var headFilter = Q(ctx, "head"); var baseFilter = Q(ctx, "base");
            var headOwner = headFilter.Contains(':') ? headFilter[..headFilter.IndexOf(':')] : null;
            var headRef = headFilter.Contains(':') ? headFilter[(headFilter.IndexOf(':') + 1)..] : headFilter;
            List<GhPull> all;
            lock (sim.Store.Lock)
                all = r!.Pulls.Where(p => state == "all" || p.State == state)
                    .Where(p => headRef.Length == 0 || (p.HeadRef == headRef && (headOwner is null || headOwner.Equals(r.Org, StringComparison.OrdinalIgnoreCase))))
                    .Where(p => baseFilter.Length == 0 || p.BaseRef == baseFilter)
                    .OrderByDescending(p => p.Number).ToList();
            await RefreshPullsAsync(sim, r!, all);
            return Results.Json(Page(ctx, all).Select(p => GhJson.Pull(sim, ctx, r!, p)).ToArray());
        });

        api.MapGet("/repos/{org}/{repo}/pulls/{number:int}", async (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            GhPull? pull; lock (sim.Store.Lock) pull = r!.Pulls.FirstOrDefault(p => p.Number == number);
            if (pull is null) return NotFound();
            await RefreshPullsAsync(sim, r!, [pull]);
            return Results.Json(GhJson.Pull(sim, ctx, r!, pull));
        });

        api.MapPatch("/repos/{org}/{repo}/pulls/{number:int}", async (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            lock (sim.Store.Lock)
            {
                var pull = r!.Pulls.FirstOrDefault(p => p.Number == number);
                if (pull is null) return NotFound();
                pull.Title = Str(body, "title") ?? pull.Title;
                pull.Body = Str(body, "body") ?? pull.Body;
                if (Str(body, "state") is { } state && state is "open" or "closed" && !pull.Merged)
                {
                    pull.State = state; pull.ClosedAt = state == "closed" ? sim.Now : null;
                }
                pull.UpdatedAt = sim.Now;
                sim.Store.Save();
                return Results.Json(GhJson.Pull(sim, ctx, r, pull));
            }
        });

        api.MapPut("/repos/{org}/{repo}/pulls/{number:int}/merge", async (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            var method = Str(body, "merge_method") ?? "merge";
            if (method is not ("merge" or "squash")) return Error(405, "Merge method not supported by this simulator");
            var bare = sim.Store.BarePath(r!);
            using var _ = await Lock(sim, r!);
            GhPull? pull; lock (sim.Store.Lock) pull = r!.Pulls.FirstOrDefault(p => p.Number == number);
            if (pull is null) return NotFound();
            if (pull.State != "open" || pull.Draft) return Error(405, "Pull Request is not mergeable");
            var baseSha = await RepoGit.RevParseAsync(bare, "refs/heads/" + pull.BaseRef);
            var headSha = await RepoGit.RevParseAsync(bare, "refs/heads/" + pull.HeadRef);
            if (baseSha is null || headSha is null) return Error(405, "Pull Request is not mergeable");
            var tree = await RepoGit.MergeTreeAsync(bare, baseSha, headSha);
            if (tree is null) return Error(409, "Merge conflict");
            var login = GhSim.Auth(ctx).Name;
            var identity = (login, $"{login}@users.noreply.sim.test");
            var title = Str(body, "commit_title") ?? (method == "merge" ? $"Merge pull request #{number} from {r.Org}/{pull.HeadRef}" : $"{pull.Title} (#{number})");
            var parents = method == "merge" ? new[] { baseSha, headSha } : new[] { baseSha };
            var merged = await RepoGit.CommitTreeAsync(bare, tree, parents, title + "\n\n" + pull.Title, identity, identity, sim.Now);
            await RepoGit.UpdateRefAsync(bare, "refs/heads/" + pull.BaseRef, merged, baseSha);
            lock (sim.Store.Lock)
            {
                pull.State = "closed"; pull.Merged = true; pull.MergeCommitSha = merged; pull.ClosedAt = sim.Now; pull.UpdatedAt = sim.Now;
                r.PushedAt = sim.Now;
                sim.Store.Save();
            }
            return Results.Json(new { sha = merged, merged = true, message = "Pull Request successfully merged" });
        });

        // ---- issues ------------------------------------------------------------------
        api.MapPost("/repos/{org}/{repo}/issues", async (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            var title = Str(body, "title");
            if (string.IsNullOrWhiteSpace(title)) return Error(422, "Validation Failed", new { resource = "Issue", code = "missing_field", field = "title" });
            var labels = new List<string>();
            if (body.TryGetProperty("labels", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in l.EnumerateArray())
                    labels.Add(item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString()! : Str(item, "name") ?? "");
            GhIssue issue;
            lock (sim.Store.Lock)
            {
                var now = sim.Now;
                issue = new GhIssue
                {
                    Number = r!.NextNumber++, Title = title, Body = Str(body, "body"), Labels = labels.Where(x => x.Length > 0).ToList(),
                    Author = GhSim.Auth(ctx).Name, CreatedAt = now, UpdatedAt = now
                };
                r.Issues.Add(issue);
                sim.Store.Save();
            }
            return Results.Json(GhJson.Issue(ctx, r!, issue), statusCode: 201);
        });

        api.MapGet("/repos/{org}/{repo}/issues", (HttpContext ctx, string org, string repo) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            var state = Q(ctx, "state") is { Length: > 0 } s ? s : "open";
            List<object> all;
            lock (sim.Store.Lock)
            {
                var issues = r!.Issues.Where(i => state == "all" || i.State == state).Select(i => (i.Number, Json: GhJson.Issue(ctx, r, i)));
                var pulls = r.Pulls.Where(p => state == "all" || p.State == state).Select(p => (p.Number, Json: GhJson.Issue(ctx, r,
                    new GhIssue { Number = p.Number, Title = p.Title, Body = p.Body, State = p.State, Author = p.Author, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt, ClosedAt = p.ClosedAt }, p)));
                all = issues.Concat(pulls).OrderByDescending(x => x.Number).Select(x => x.Json).ToList();
            }
            return Results.Json(Page(ctx, all));
        });

        api.MapGet("/repos/{org}/{repo}/issues/{number:int}", (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo);
            if (error is not null) return error;
            lock (sim.Store.Lock)
            {
                var issue = r!.Issues.FirstOrDefault(i => i.Number == number);
                if (issue is not null) return Results.Json(GhJson.Issue(ctx, r, issue));
                var p = r.Pulls.FirstOrDefault(x => x.Number == number);
                return p is null ? NotFound() : Results.Json(GhJson.Issue(ctx, r,
                    new GhIssue { Number = p.Number, Title = p.Title, Body = p.Body, State = p.State, Author = p.Author, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt, ClosedAt = p.ClosedAt }, p));
            }
        });

        api.MapPatch("/repos/{org}/{repo}/issues/{number:int}", async (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            lock (sim.Store.Lock)
            {
                var issue = r!.Issues.FirstOrDefault(i => i.Number == number);
                if (issue is null) return NotFound();
                issue.Title = Str(body, "title") ?? issue.Title;
                issue.Body = Str(body, "body") ?? issue.Body;
                if (Str(body, "state") is { } state && state is "open" or "closed")
                {
                    issue.State = state; issue.ClosedAt = state == "closed" ? sim.Now : null;
                }
                issue.UpdatedAt = sim.Now;
                sim.Store.Save();
                return Results.Json(GhJson.Issue(ctx, r, issue));
            }
        });

        api.MapPost("/repos/{org}/{repo}/issues/{number:int}/comments", async (HttpContext ctx, string org, string repo, int number) =>
        {
            var (r, error) = Open(sim, ctx, org, repo, write: true);
            if (error is not null) return error;
            var body = await BodyAsync(ctx);
            if (string.IsNullOrWhiteSpace(Str(body, "body"))) return Error(422, "Validation Failed");
            lock (sim.Store.Lock)
            {
                var issue = r!.Issues.FirstOrDefault(i => i.Number == number);
                if (issue is null) return NotFound();
                var comment = new GhComment { Id = r.NextCommentId++, Body = Str(body, "body")!, Author = GhSim.Auth(ctx).Name, CreatedAt = sim.Now };
                issue.Comments.Add(comment);
                sim.Store.Save();
                return Results.Json(new { id = comment.Id, body = comment.Body, user = new { login = comment.Author }, created_at = Iso(comment.CreatedAt) }, statusCode: 201);
            }
        });
    }

    // A pull request tracks its branches: report the live head and whether it was merged by a push.
    private static async Task RefreshPullsAsync(GhSim sim, GhRepo repo, IEnumerable<GhPull> pulls)
    {
        var bare = sim.Store.BarePath(repo);
        foreach (var p in pulls.Where(p => p.State == "open").ToList())
        {
            var head = await RepoGit.RevParseAsync(bare, "refs/heads/" + p.HeadRef);
            if (head is not null) p.HeadSha = head;
            var baseSha = await RepoGit.RevParseAsync(bare, "refs/heads/" + p.BaseRef);
            if (baseSha is not null) p.BaseSha = baseSha;
        }
    }
}
