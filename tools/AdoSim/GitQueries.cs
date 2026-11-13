using System.Globalization;
using System.Text.RegularExpressions;

namespace AdoSim;

public sealed record TreeEntry(string Mode, string Type, string ObjectId, string Path);

public sealed record CommitInfo(
    string Id, string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate, string CommitterName,
    string CommitterEmail, DateTimeOffset CommitterDate, string[] Parents, string Tree, string Message);

// Read-only helpers over the bare repositories (REST: items, commits, branch stats).
public static class GitQueries
{
    private const string LogFormat = "%H%x1f%an%x1f%ae%x1f%at%x1f%cn%x1f%ce%x1f%ct%x1f%P%x1f%T%x1f%B%x1e";
    private static readonly Regex Sha = new("^[0-9a-fA-F]{4,64}$", RegexOptions.Compiled);

    // versionType: branch (default) | tag | commit. Returns null for anything that could be read as an option.
    public static string? VersionRev(SimRepo repo, string? version, string? versionType)
    {
        if (version is { Length: > 0 } && version.StartsWith('-')) return null;
        return (versionType ?? "branch").ToLowerInvariant() switch
        {
            "commit" => version,
            "tag" => version is null ? null : "refs/tags/" + version,
            _ => version is null ? repo.DefaultRef : version.StartsWith("refs/", StringComparison.Ordinal) ? version : "refs/heads/" + version
        };
    }

    public static async Task<string?> CommitOfAsync(SimRepo repo, string? rev) =>
        rev is null ? null : await Git.RevParseAsync(repo, rev + "^{commit}");

    public static async Task<string?> TreeOfAsync(SimRepo repo, string commit) => await Git.RevParseAsync(repo, commit + "^{tree}");

    public static async Task<List<TreeEntry>> LsTreeAsync(SimRepo repo, string commit, string path, bool children, bool recursive)
    {
        var args = new List<string> { "ls-tree", "-z" };
        if (recursive) args.AddRange(["-r", "-t"]);
        args.Add(commit);
        if (path.Length > 0)
        {
            args.Add("--");
            args.Add(children ? path + "/" : path);
        }
        var (exit, output, _) = await Git.RunAsync(repo.BarePath, null, [.. args]);
        var entries = new List<TreeEntry>();
        if (exit != 0) return entries;
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ');
            entries.Add(new TreeEntry(meta[0], meta[1], meta[2], record[(tab + 1)..]));
        }
        return entries;
    }

    public static async Task<byte[]?> BlobAsync(SimRepo repo, string objectId)
    {
        var (exit, bytes, _) = await Git.RunBytesAsync(repo.BarePath, null, "cat-file", "blob", objectId);
        return exit == 0 ? bytes : null;
    }

    public static bool IsBinary(byte[] content) => Array.IndexOf(content, (byte)0, 0, Math.Min(content.Length, 8000)) >= 0;

    private static CommitInfo? Parse(string record)
    {
        var f = record.TrimStart('\n').Split('\x1f');
        if (f.Length < 10) return null;
        static DateTimeOffset Date(string unix) => DateTimeOffset.FromUnixTimeSeconds(long.Parse(unix, CultureInfo.InvariantCulture));
        return new CommitInfo(f[0], f[1], f[2], Date(f[3]), f[4], f[5], Date(f[6]),
            f[7].Split(' ', StringSplitOptions.RemoveEmptyEntries), f[8], string.Join('\x1f', f[9..]).TrimEnd('\n'));
    }

    public static async Task<List<CommitInfo>> LogAsync(SimRepo repo, string rev, int top, int skip,
        string? path = null, string? author = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var args = new List<string> { "log", "--format=" + LogFormat, $"--max-count={Math.Max(top, 0)}", $"--skip={Math.Max(skip, 0)}" };
        if (!string.IsNullOrEmpty(author)) args.Add("--author=" + author);
        if (from is { } f) args.Add("--since=" + f.ToUnixTimeSeconds());
        if (to is { } t) args.Add("--until=" + t.ToUnixTimeSeconds());
        args.Add(rev);
        if (!string.IsNullOrEmpty(path)) { args.Add("--"); args.Add(path.Trim('/')); }
        var (exit, output, _) = await Git.RunAsync(repo.BarePath, null, [.. args]);
        return exit != 0 ? [] : output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<CommitInfo>().ToList();
    }

    public static async Task<CommitInfo?> CommitAsync(SimRepo repo, string id)
    {
        if (!Sha.IsMatch(id)) return null;
        var (exit, output, _) = await Git.RunAsync(repo.BarePath, null, "show", "-s", "--format=" + LogFormat, id + "^{commit}");
        return exit != 0 ? null : Parse(output.Split('\x1e')[0]);
    }

    // branch -> (ahead, behind) relative to the base commit.
    public static async Task<List<(string Branch, string Commit, int Ahead, int Behind)>> BranchStatsAsync(SimRepo repo, string baseCommit)
    {
        var (exit, output, _) = await Git.RunAsync(repo.BarePath, null, "for-each-ref",
            $"--format=%(refname) %(objectname) %(ahead-behind:{baseCommit})", "refs/heads");
        var result = new List<(string, string, int, int)>();
        if (exit == 0)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(' ');
                if (p.Length == 4 && int.TryParse(p[2], out var ahead) && int.TryParse(p[3], out var behind))
                    result.Add((p[0]["refs/heads/".Length..], p[1], ahead, behind));
            }
            return result;
        }
        // git older than 2.41 has no ahead-behind atom.
        foreach (var (name, objectId) in (await Git.RefsAsync(repo)).Where(r => r.Name.StartsWith("refs/heads/")))
        {
            var (code, counts, _) = await Git.RunAsync(repo.BarePath, null, "rev-list", "--left-right", "--count", $"{baseCommit}...{objectId}");
            var parts = counts.Split(['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (code == 0 && parts.Length == 2) result.Add((name["refs/heads/".Length..], objectId, int.Parse(parts[1]), int.Parse(parts[0])));
        }
        return result;
    }
}
