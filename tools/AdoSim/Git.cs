using System.Diagnostics;
using System.Text;

namespace AdoSim;

public static class Git
{
    private static ProcessStartInfo Info(string? workDir, IDictionary<string, string>? env, string[] args, Encoding? encoding)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            StandardOutputEncoding = encoding, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        return info;
    }

    public static async Task<(int Exit, string Out, string Err)> RunAsync(
        string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        using var process = Process.Start(Info(workDir, env, args, Encoding.UTF8))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    // Binary-safe variant for blob content.
    public static async Task<(int Exit, byte[] Out, string Err)> RunBytesAsync(
        string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        using var process = Process.Start(Info(workDir, env, args, null))!;
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        var stderr = process.StandardError.ReadToEndAsync();
        await copy;
        await process.WaitForExitAsync();
        return (process.ExitCode, buffer.ToArray(), await stderr);
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
        var (exit, output, _) = await RunAsync(repo.BarePath, null, "show", $"{repo.DefaultRef}:{path.TrimStart('/')}");
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
