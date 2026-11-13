using System.Diagnostics;
using System.Text;

namespace AdoSim;

public static class Git
{
    public static async Task<(int Exit, string Out, string Err)> RunAsync(
        string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    public static async Task<string> MustAsync(string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        var (exit, output, error) = await RunAsync(workDir, env, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }

    // Fixed identity and dates make commit SHAs reproducible between runs and machines.
    public static Dictionary<string, string> Deterministic(int day) => new()
    {
        ["GIT_AUTHOR_NAME"] = "Sim Author", ["GIT_AUTHOR_EMAIL"] = "sim@example.test",
        ["GIT_COMMITTER_NAME"] = "Sim Author", ["GIT_COMMITTER_EMAIL"] = "sim@example.test",
        ["GIT_AUTHOR_DATE"] = $"2024-01-{day:00}T10:00:00Z", ["GIT_COMMITTER_DATE"] = $"2024-01-{day:00}T10:00:00Z"
    };

    // Builds a bare repository once; an existing one (persisted volume) is reused.
    public static async Task CreateRepositoryAsync(SimRepo repo, string dataDir)
    {
        if (Directory.Exists(repo.BarePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(repo.BarePath)!);
        var spec = repo.Spec;
        if (spec.Empty || spec.Commits < 1)
        {
            await MustAsync(null, null, "init", "--bare", "-b", spec.DefaultBranch, repo.BarePath);
            return;
        }
        var work = Path.Combine(dataDir, "work", repo.Id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            await MustAsync(work, null, "init", "-b", spec.DefaultBranch);
            await MustAsync(work, null, "config", "core.autocrlf", "false");
            await MustAsync(work, null, "config", "commit.gpgsign", "false");
            for (var i = 1; i <= spec.Commits; i++)
            {
                File.WriteAllText(Path.Combine(work, "README.md"), $"# {spec.Name}\nrevision {i}\n");
                Directory.CreateDirectory(Path.Combine(work, "src"));
                File.WriteAllText(Path.Combine(work, "src", $"file{i}.txt"), $"{spec.Name} content {i}\n");
                if (i == 1)
                    foreach (var (path, content) in spec.Files ?? [])
                    {
                        var full = Path.Combine(work, path.TrimStart('/'));
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        File.WriteAllText(full, content);
                    }
                await MustAsync(work, null, "add", "-A");
                await MustAsync(work, Deterministic(i), "commit", "-m", $"Commit {i} of {spec.Name}");
            }
            var day = spec.Commits + 1;
            foreach (var branch in spec.Branches ?? [])
            {
                await MustAsync(work, null, "checkout", "-q", "-b", branch, spec.DefaultBranch);
                File.WriteAllText(Path.Combine(work, "src", "branch.txt"), branch);
                await MustAsync(work, null, "add", "-A");
                await MustAsync(work, Deterministic(day++ % 28 + 1), "commit", "-m", $"Work on {branch}");
            }
            await MustAsync(work, null, "checkout", "-q", spec.DefaultBranch);
            foreach (var tag in spec.Tags ?? []) await MustAsync(work, null, "tag", tag);
            foreach (var tag in spec.AnnotatedTags ?? [])
                await MustAsync(work, Deterministic(day % 28 + 1), "tag", "-a", tag, "-m", $"Release {tag}");
            for (var i = 0; i < spec.ExtraBranches; i++) await MustAsync(work, null, "branch", $"bulk/branch-{i:0000}");
            for (var i = 0; i < spec.ExtraTags; i++) await MustAsync(work, null, "tag", $"bulk-tag-{i:0000}");
            await MustAsync(null, null, "clone", "--bare", "--quiet", work, repo.BarePath);
        }
        finally { try { Directory.Delete(work, true); } catch { /* best effort */ } }
    }

    public static async Task<List<(string Name, string ObjectId)>> RefsAsync(SimRepo repo)
    {
        var output = await MustAsync(repo.BarePath, null, "for-each-ref", "--format=%(objectname) %(refname)");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Name: line[41..], ObjectId: line[..40]))
            .Where(r => r.Name.StartsWith("refs/heads/") || r.Name.StartsWith("refs/tags/"))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static async Task<string?> ShowFileAsync(SimRepo repo, string path)
    {
        var (exit, output, _) = await RunAsync(repo.BarePath, null, "show", $"{repo.Spec.DefaultBranch}:{path.TrimStart('/')}");
        return exit == 0 ? output : null;
    }

    public static async Task<string?> RevParseAsync(SimRepo repo, string reference)
    {
        var (exit, output, _) = await RunAsync(repo.BarePath, null, "rev-parse", "--verify", "--quiet", reference);
        return exit == 0 ? output.Trim() : null;
    }

    public static Task WriteAsync(Stream target, string text) =>
        target.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
}
