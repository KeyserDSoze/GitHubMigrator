using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace GhSim;

public static partial class GhSimApp
{
    private static void MapSmartHttp(WebApplication app, GhSim sim)
    {
        app.MapGet("/{org}/{repoGit}/info/refs", async (HttpContext ctx, string org, string repoGit) =>
        {
            var service = Q(ctx, "service");
            if (service is not ("git-upload-pack" or "git-receive-pack")) return Results.StatusCode(403);
            var (r, error) = OpenForGit(sim, ctx, org, repoGit, write: service == "git-receive-pack");
            if (error is not null) return error;
            ctx.Response.ContentType = $"application/x-{service}-advertisement";
            ctx.Response.Headers.CacheControl = "no-cache";
            var banner = $"# service={service}\n";
            await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes($"{banner.Length + 4:x4}{banner}0000"));
            await StreamGitAsync(ctx, sim, r!, null, service["git-".Length..], "--stateless-rpc", "--advertise-refs");
            return Results.Empty;
        });

        app.MapPost("/{org}/{repoGit}/git-upload-pack", async (HttpContext ctx, string org, string repoGit) =>
        {
            var (r, error) = OpenForGit(sim, ctx, org, repoGit, write: false);
            if (error is not null) return error;
            ctx.Response.ContentType = "application/x-git-upload-pack-result";
            ctx.Response.Headers.CacheControl = "no-cache";
            await StreamGitAsync(ctx, sim, r!, RequestBody(ctx), "upload-pack", "--stateless-rpc");
            return Results.Empty;
        });

        app.MapPost("/{org}/{repoGit}/git-receive-pack", async (HttpContext ctx, string org, string repoGit) =>
        {
            var (r, error) = OpenForGit(sim, ctx, org, repoGit, write: true);
            if (error is not null) return error;
            ctx.Response.ContentType = "application/x-git-receive-pack-result";
            ctx.Response.Headers.CacheControl = "no-cache";
            using var _ = await Lock(sim, r!);
            var hadHeads = await RepoGit.HasHeadsAsync(sim.Store.BarePath(r!));
            await StreamGitAsync(ctx, sim, r!, RequestBody(ctx), "receive-pack", "--stateless-rpc");
            await AfterPushAsync(sim, r!, hadHeads);
            return Results.Empty;
        });
    }

    private static Stream RequestBody(HttpContext ctx) =>
        ctx.Request.Headers.ContentEncoding == "gzip"
            ? new GZipStream(ctx.Request.Body, CompressionMode.Decompress) : ctx.Request.Body;

    // Anonymous callers get a 401 challenge (git retries with credentials); anything the caller
    // cannot see is 404, a read-only credential on a push is 403, exactly like github.com.
    private static (GhRepo? Repo, IResult? Error) OpenForGit(GhSim sim, HttpContext ctx, string org, string repoGit, bool write)
    {
        var who = GhSim.Auth(ctx);
        if (who.Kind is PrincipalKind.Anonymous or PrincipalKind.App)
        {
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"GitHub\"";
            return (null, Results.Text("Authentication required", statusCode: 401));
        }
        var name = repoGit.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repoGit[..^4] : repoGit;
        GhRepo? repo; lock (sim.Store.Lock) repo = sim.Store.Repo(org, name);
        if (repo is null || !sim.CanRead(who, repo)) return (null, Results.Text("Repository not found.", statusCode: 404));
        if (write && !sim.CanWrite(who, repo))
            return (null, Results.Text($"Permission to {repo.FullName}.git denied to {who.Name}.", statusCode: 403));
        return (repo, null);
    }

    private static async Task StreamGitAsync(HttpContext ctx, GhSim sim, GhRepo repo, Stream? input, params string[] args)
    {
        var bare = sim.Store.BarePath(repo);
        var info = new ProcessStartInfo("git")
        { RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        GitCli.Isolate(info);
        if (args[0] == "receive-pack")
        {
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("receive.fsckObjects=true");
            info.ArgumentList.Add("-c"); info.ArgumentList.Add($"receive.maxInputSize={sim.Options.MaxPushBytes}");
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("receive.advertisePushOptions=false");
        }
        foreach (var a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add(bare);
        using var process = Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        var pump = Task.Run(async () =>
        {
            if (input is null) return;
            try
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, ctx.RequestAborted);
                process.StandardInput.Close();
            }
            catch (IOException) { /* git exited early (e.g. a rejected push) */ }
        });
        await process.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        await pump;
        await process.WaitForExitAsync(ctx.RequestAborted);
    }

    // The first branch pushed to an empty repository becomes its default branch (like github.com).
    private static async Task AfterPushAsync(GhSim sim, GhRepo repo, bool hadHeads)
    {
        var bare = sim.Store.BarePath(repo);
        if (!hadHeads && await RepoGit.HasHeadsAsync(bare) && await RepoGit.RevParseAsync(bare, "refs/heads/" + repo.DefaultBranch) is null)
        {
            var heads = (await RepoGit.RefsAsync(bare)).Where(x => x.Name.StartsWith("refs/heads/")).Select(x => x.Name["refs/heads/".Length..]).ToList();
            var chosen = heads.Contains("main") ? "main" : heads.Contains("master") ? "master" : heads[0];
            await RepoGit.SetDefaultBranchAsync(bare, chosen);
            lock (sim.Store.Lock) repo.DefaultBranch = chosen;
            await WriteRulesAsync(sim, repo);
        }
        Touch(sim, repo);
    }
}
