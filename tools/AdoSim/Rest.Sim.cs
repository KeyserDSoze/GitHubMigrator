using System.IO.Compression;

namespace AdoSim;

public static partial class AdoSimApp
{
    private static void MapSimControl(WebApplication app, SimModel model)
    {
        app.MapGet("/_sim/expected", async () => Results.Json(await ExpectedAsync(model)));

        // Incremental changes for end-to-end tests: a real commit on a real branch.
        app.MapPost("/_sim/repos/{project}/{repo}/commits", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return NotFound();
            if (r.Spec.Disabled) return Results.Json(new { message = "TF401019: repository is disabled" }, statusCode: 403);
            CommitRequest? request;
            try { request = await ctx.Request.ReadFromJsonAsync<CommitRequest>(SeedSpec.JsonOptions); }
            catch (System.Text.Json.JsonException) { return Bad("Invalid JSON body."); }
            if (request is null) return Bad("A JSON body is required.");
            try
            {
                using var _ = await model.LockRepoAsync(r);
                var commitId = await RepoBuilder.CommitAsync(r, request);
                var refs = (await Git.RefsAsync(r)).Select(x => new { name = x.Name, objectId = x.ObjectId }).ToArray();
                var branch = request.Branch!.StartsWith("refs/heads/", StringComparison.Ordinal) ? request.Branch["refs/heads/".Length..] : request.Branch;
                return Results.Json(new { commitId, branch, refs }, statusCode: 201);
            }
            catch (SimRequestException e) { return Results.Json(new { message = e.Message }, statusCode: e.Status); }
        });

        app.MapPost("/_sim/reset", async () =>
        {
            await Estate.ResetAsync(model);
            return Results.Json(await ExpectedAsync(model));
        });
    }

    private static void MapGitSmartHttp(WebApplication app, SimModel model, string basePath)
    {
        var prefix = basePath + "/{collection}/{project}/_git/{repo}";
        app.MapGet(prefix + "/info/refs", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            var service = Q(ctx, "service");
            if (service == "git-receive-pack")
            {
                if (r is null) return Results.StatusCode(404);
                if (r.Spec.Disabled) return Results.Json(new { message = "TF401019: repository is disabled" }, statusCode: 403);
                ctx.Response.ContentType = "application/x-git-receive-pack-advertisement";
                ctx.Response.Headers.CacheControl = "no-cache";
                await Git.WriteAsync(ctx.Response.Body, "001f# service=git-receive-pack\n0000");
                await StreamGit(ctx, null, "receive-pack", "--stateless-rpc", "--advertise-refs", r.BarePath);
                return Results.Empty;
            }
            if (r is null || service != "git-upload-pack") return Results.StatusCode(403);
            ctx.Response.ContentType = "application/x-git-upload-pack-advertisement";
            await Git.WriteAsync(ctx.Response.Body, "001e# service=git-upload-pack\n0000");
            await StreamGit(ctx, null, "upload-pack", "--stateless-rpc", "--advertise-refs", r.BarePath);
            return Results.Empty;
        });
        app.MapPost(prefix + "/git-upload-pack", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return Results.StatusCode(404);
            ctx.Response.ContentType = "application/x-git-upload-pack-result";
            await StreamGit(ctx, Body(ctx), "upload-pack", "--stateless-rpc", r.BarePath);
            return Results.Empty;
        });
        app.MapPost(prefix + "/git-receive-pack", async (HttpContext ctx, string project, string repo) =>
        {
            var r = FindRepo(model, project, repo);
            if (r is null) return Results.StatusCode(404);
            if (r.Spec.Disabled) return Results.Json(new { message = "TF401019: repository is disabled" }, statusCode: 403);
            ctx.Response.ContentType = "application/x-git-receive-pack-result";
            await StreamGit(ctx, Body(ctx), "receive-pack", "--stateless-rpc", r.BarePath);
            return Results.Empty;
        });
    }

    private static Stream Body(HttpContext ctx) =>
        ctx.Request.Headers.ContentEncoding == "gzip" ? new GZipStream(ctx.Request.Body, CompressionMode.Decompress) : ctx.Request.Body;

    private static async Task StreamGit(HttpContext ctx, Stream? input, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        { RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, ctx.RequestAborted);
            process.StandardInput.Close();
        }
        await process.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        await process.WaitForExitAsync(ctx.RequestAborted);
    }
}
