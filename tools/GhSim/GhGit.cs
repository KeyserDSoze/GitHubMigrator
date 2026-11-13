using System.Diagnostics;
using System.Text;

namespace GhSim;

public sealed record RefInfo(string Name, string ObjectId, string ObjectType);
public sealed record CommitInfo(string Sha, string Tree, string[] Parents, string Message,
    string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate,
    string CommitterName, string CommitterEmail, DateTimeOffset CommitterDate);
public sealed record TagInfo(string Sha, string Tag, string TargetSha, string TargetType, string Message,
    string TaggerName, string TaggerEmail, DateTimeOffset TaggerDate);
public sealed record TreeEntry(string Mode, string Type, string Sha, string Path);

public sealed class GitConflictException(string message) : Exception(message);

public static class GitCli
{
    public static async Task<(int Exit, byte[] Out, string Err)> RunBytesAsync(
        string? workDir, IDictionary<string, string>? env, byte[]? stdin, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        Isolate(info);
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = ReadAllAsync(process.StandardOutput.BaseStream);
        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    public static void Isolate(ProcessStartInfo info)
    {
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    }

    public static async Task<(int Exit, string Out, string Err)> RunAsync(
        string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        var (exit, output, error) = await RunBytesAsync(workDir, env, null, args);
        return (exit, Encoding.UTF8.GetString(output), error);
    }

    public static async Task<string> MustAsync(string? workDir, IDictionary<string, string>? env, params string[] args)
    {
        var (exit, output, error) = await RunAsync(workDir, env, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}

// Operations on one bare repository. Writes go through git plumbing and a throw-away index
// so the bare repository never needs a working tree.
public static class RepoGit
{
    public const string ZeroOid = "0000000000000000000000000000000000000000";

    public static async Task InitAsync(string barePath, string defaultBranch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(barePath)!);
        await GitCli.MustAsync(null, null, "init", "--bare", "-q", "-b", defaultBranch, barePath);
        await GitCli.MustAsync(barePath, null, "config", "receive.fsckObjects", "true");
        await GitCli.MustAsync(barePath, null, "config", "gc.auto", "0");
    }

    public static async Task<List<RefInfo>> RefsAsync(string barePath)
    {
        var output = await GitCli.MustAsync(barePath, null, "for-each-ref", "--format=%(objectname)%09%(objecttype)%09%(refname)");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(p => p.Length == 3 && (p[2].StartsWith("refs/heads/") || p[2].StartsWith("refs/tags/")))
            .Select(p => new RefInfo(p[2], p[0], p[1]))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static async Task<string?> RevParseAsync(string barePath, string rev)
    {
        // "<rev>:<path>" must not get a peel suffix: git would read it as part of the path.
        var spec = rev.Contains(':') ? rev : rev + "^{object}";
        var (exit, output, _) = await GitCli.RunAsync(barePath, null, "rev-parse", "--verify", "--quiet", spec);
        return exit == 0 ? output.Trim() : null;
    }

    public static async Task<string?> ObjectTypeAsync(string barePath, string rev)
    {
        var (exit, output, _) = await GitCli.RunAsync(barePath, null, "cat-file", "-t", rev);
        return exit == 0 ? output.Trim() : null;
    }

    public static async Task<bool> HasHeadsAsync(string barePath) =>
        !string.IsNullOrWhiteSpace((await GitCli.RunAsync(barePath, null, "for-each-ref", "--count=1", "refs/heads/")).Out);

    public static async Task SetDefaultBranchAsync(string barePath, string branch) =>
        await GitCli.MustAsync(barePath, null, "symbolic-ref", "HEAD", "refs/heads/" + branch);

    public static async Task<CommitInfo?> CommitAsync(string barePath, string rev)
    {
        var (exit, output, _) = await GitCli.RunAsync(barePath, null, "cat-file", "commit", rev);
        if (exit != 0) return null;
        var split = output.IndexOf("\n\n", StringComparison.Ordinal);
        var header = split < 0 ? output : output[..split];
        var message = split < 0 ? "" : output[(split + 2)..];
        string tree = "", an = "", ae = "", cn = "", ce = "";
        DateTimeOffset ad = default, cd = default;
        var parents = new List<string>();
        foreach (var line in header.Split('\n'))
        {
            if (line.StartsWith("tree ")) tree = line[5..];
            else if (line.StartsWith("parent ")) parents.Add(line[7..]);
            else if (line.StartsWith("author ")) (an, ae, ad) = Person(line[7..]);
            else if (line.StartsWith("committer ")) (cn, ce, cd) = Person(line[10..]);
        }
        var sha = await RevParseAsync(barePath, rev) ?? rev;
        return new CommitInfo(sha, tree, [.. parents], message, an, ae, ad, cn, ce, cd);
    }

    public static async Task<TagInfo?> TagAsync(string barePath, string sha)
    {
        if (await ObjectTypeAsync(barePath, sha) != "tag") return null;
        var output = await GitCli.MustAsync(barePath, null, "cat-file", "tag", sha);
        var split = output.IndexOf("\n\n", StringComparison.Ordinal);
        var header = split < 0 ? output : output[..split];
        var message = split < 0 ? "" : output[(split + 2)..];
        string target = "", type = "", tag = "", tn = "", te = "";
        DateTimeOffset td = default;
        foreach (var line in header.Split('\n'))
        {
            if (line.StartsWith("object ")) target = line[7..];
            else if (line.StartsWith("type ")) type = line[5..];
            else if (line.StartsWith("tag ")) tag = line[4..];
            else if (line.StartsWith("tagger ")) (tn, te, td) = Person(line[7..]);
        }
        return new TagInfo(sha, tag, target, type, message, tn, te, td);
    }

    private static (string Name, string Email, DateTimeOffset Date) Person(string value)
    {
        var open = value.IndexOf('<'); var close = value.IndexOf('>');
        if (open < 0 || close < open) return (value, "", default);
        var rest = value[(close + 1)..].Trim().Split(' ');
        var date = rest.Length > 0 && long.TryParse(rest[0], out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : default;
        return (value[..open].Trim(), value[(open + 1)..close], date);
    }

    public static async Task<List<TreeEntry>> TreeAsync(string barePath, string rev, string? path = null, bool recursive = false)
    {
        var args = new List<string> { "ls-tree", "-z" };
        if (recursive) args.Add("-r");
        args.Add(rev);
        if (!string.IsNullOrEmpty(path)) { args.Add("--"); args.Add(path); }
        var (exit, output, _) = await GitCli.RunAsync(barePath, null, [.. args]);
        if (exit != 0) return [];
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            var tab = entry.IndexOf('\t');
            var meta = entry[..tab].Split(' ');
            return new TreeEntry(meta[0], meta[1], meta[2], entry[(tab + 1)..]);
        }).ToList();
    }

    public static async Task<byte[]?> BlobAsync(string barePath, string sha)
    {
        var (exit, output, _) = await GitCli.RunBytesAsync(barePath, null, null, "cat-file", "blob", sha);
        return exit == 0 ? output : null;
    }

    public static async Task<bool> IsAncestorAsync(string barePath, string ancestor, string descendant) =>
        (await GitCli.RunAsync(barePath, null, "merge-base", "--is-ancestor", ancestor, descendant)).Exit == 0;

    public static async Task UpdateRefAsync(string barePath, string name, string newOid, string? oldOid)
    {
        var (exit, _, error) = await GitCli.RunAsync(barePath, null, "update-ref", name, newOid, oldOid ?? ZeroOid);
        if (exit != 0) throw new GitConflictException("Reference update failed: " + error.Trim());
    }

    public static Task DeleteRefAsync(string barePath, string name) =>
        GitCli.MustAsync(barePath, null, "update-ref", "-d", name);

    public static async Task<(string CommitSha, string BlobSha)> CommitFileAsync(
        string barePath, string branch, string path, byte[]? content, string message,
        (string Name, string Email) author, (string Name, string Email) committer, DateTimeOffset when)
    {
        var (commit, blobs) = await CommitFilesAsync(barePath, branch, [(path, content)], message, author, committer, when);
        return (commit, blobs[0]);
    }

    // Writes (content) or removes (null content) files and commits them on the branch in one commit.
    // The branch is created when the repository has no commits yet.
    public static async Task<(string CommitSha, string[] BlobShas)> CommitFilesAsync(
        string barePath, string branch, IReadOnlyList<(string Path, byte[]? Content)> edits, string message,
        (string Name, string Email) author, (string Name, string Email) committer, DateTimeOffset when)
    {
        var current = await RevParseAsync(barePath, "refs/heads/" + branch);
        var index = Path.Combine(Path.GetTempPath(), "ghsim-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            if (current is not null) await GitCli.MustAsync(barePath, env, "read-tree", current);
            var blobs = new List<string>();
            foreach (var (path, content) in edits)
            {
                if (content is null)
                {
                    // --force-remove needs a work tree; a mode-0 index-info entry removes the path in a bare repo.
                    var removal = Encoding.UTF8.GetBytes($"0 {ZeroOid}\t{path}\n");
                    var (removed, _, removeError) = await GitCli.RunBytesAsync(barePath, env, removal, "update-index", "--index-info");
                    if (removed != 0) throw new InvalidOperationException("update-index failed: " + removeError);
                    blobs.Add("");
                    continue;
                }
                var (exit, output, error) = await GitCli.RunBytesAsync(barePath, null, content, "hash-object", "-w", "--stdin");
                if (exit != 0) throw new InvalidOperationException("hash-object failed: " + error);
                var blob = Encoding.ASCII.GetString(output).Trim();
                await GitCli.MustAsync(barePath, env, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
                blobs.Add(blob);
            }
            var tree = await GitCli.MustAsync(barePath, env, "write-tree");
            var commit = await CommitTreeAsync(barePath, tree, current is null ? [] : [current], message, author, committer, when);
            await UpdateRefAsync(barePath, "refs/heads/" + branch, commit, current);
            return (commit, [.. blobs]);
        }
        finally { try { File.Delete(index); } catch (IOException) { } }
    }

    public static async Task<string> CommitTreeAsync(string barePath, string tree, IEnumerable<string> parents,
        string message, (string Name, string Email) author, (string Name, string Email) committer, DateTimeOffset when)
    {
        var args = new List<string> { "commit-tree", tree };
        foreach (var parent in parents) { args.Add("-p"); args.Add(parent); }
        args.Add("-m"); args.Add(message);
        return await GitCli.MustAsync(barePath, Identity(author, committer, when), [.. args]);
    }

    public static Dictionary<string, string> Identity(
        (string Name, string Email) author, (string Name, string Email) committer, DateTimeOffset when)
    {
        var date = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        return new()
        {
            ["GIT_AUTHOR_NAME"] = author.Name, ["GIT_AUTHOR_EMAIL"] = author.Email, ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = committer.Name, ["GIT_COMMITTER_EMAIL"] = committer.Email, ["GIT_COMMITTER_DATE"] = date
        };
    }

    // Three-way merge into a new commit without touching any ref. Null means conflicts.
    public static async Task<string?> MergeTreeAsync(string barePath, string baseSha, string headSha)
    {
        var (exit, output, _) = await GitCli.RunAsync(barePath, null, "merge-tree", "--write-tree", baseSha, headSha);
        return exit == 0 ? output.Split('\n')[0].Trim() : null;
    }
}
