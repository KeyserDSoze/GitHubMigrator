using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AdoSim;

public sealed record GitPerson(string Name, string Email)
{
    public static readonly GitPerson Sim = new("Sim Author", "sim@example.test");
}

public sealed class SimRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record CommitRequest(string? Branch, string? Message, Dictionary<string, string>? Files = null,
    List<string>? Delete = null, string? CreateBranchFrom = null, string? Tag = null);

// Writes a git fast-import stream: one process per repository instead of one per commit.
internal sealed class FastImport : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private bool _finished;

    private FastImport(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static FastImport Start(string barePath)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = barePath, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "fast-import", "--quiet", "--done", "--date-format=raw" }) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        return new FastImport(Process.Start(info)!);
    }

    private void Write(byte[] bytes) => _input.Write(bytes, 0, bytes.Length);
    private void Line(string text) => Write(Encoding.UTF8.GetBytes(text + "\n"));
    private void Data(byte[] bytes) { Line($"data {bytes.Length}"); Write(bytes); Write("\n"u8.ToArray()); }

    private static string Signature(GitPerson who, long timestamp) => $"{who.Name} <{who.Email}> {timestamp} +0000";

    // C-style quoting is only required for a few characters; everything else (unicode, spaces) is raw.
    private static string Quote(string path) =>
        path.StartsWith('"') || path.Contains('\n') || path.Contains('\\')
            ? "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
            : path;

    // A null content deletes the path.
    public void Commit(string reference, int mark, GitPerson who, long timestamp, string message, string? from,
        IEnumerable<KeyValuePair<string, byte[]?>> changes)
    {
        Line($"commit {reference}");
        Line($"mark :{mark}");
        Line("author " + Signature(who, timestamp));
        Line("committer " + Signature(who, timestamp));
        Data(Encoding.UTF8.GetBytes(message.EndsWith('\n') ? message : message + "\n"));
        if (from is not null) Line($"from {from}");
        foreach (var (path, content) in changes)
        {
            if (content is null) { Line($"D {Quote(path)}"); continue; }
            Line($"M 100644 inline {Quote(path)}");
            Data(content);
        }
        Line("");
    }

    public void Tag(string name, string fromMark, GitPerson who, long timestamp, string message)
    {
        Line($"tag {name}");
        Line($"from {fromMark}");
        Line("tagger " + Signature(who, timestamp));
        Data(Encoding.UTF8.GetBytes(message + "\n"));
    }

    public void Reset(string reference, string from)
    {
        Line($"reset {reference}");
        Line($"from {from}");
        Line("");
    }

    public async Task FinishAsync()
    {
        _finished = true;
        Line("done");
        await _input.FlushAsync();
        _input.Close();
        await _process.WaitForExitAsync();
        var error = await _stderr;
        await _stdout;
        if (_process.ExitCode != 0) throw new InvalidOperationException("git fast-import failed: " + error);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            try { _process.Kill(true); } catch (InvalidOperationException) { /* already exited */ }
        }
        _process.Dispose();
        await Task.CompletedTask;
    }
}

public static class RepoBuilder
{
    // Bump whenever the way repositories are built changes: it is part of the seed hash.
    public const int Version = 1;
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly long ApiFloor = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static long Day(int day) => new DateTimeOffset(2024, 1, day, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static List<GitPerson> Authors(SimModel model, SeedRepo spec)
    {
        var people = (spec.Authors ?? []).Select(name =>
        {
            var user = model.Seed.Users.FirstOrDefault(u => u.Name == name);
            return user is null ? new GitPerson(name, name + "@users.example.test")
                : new GitPerson(user.Display, user.Mail ?? user.Name + "@users.example.test");
        }).ToList();
        return people.Count > 0 ? people : [GitPerson.Sim];
    }

    // Everything the seed ships as real content: filesFrom folder, then inline files, then generated blobs.
    private static Dictionary<string, byte[]> RealFiles(SimModel model, SeedRepo spec)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (spec.FilesFrom is not null)
            foreach (var (path, content) in model.FilesFor(spec.FilesFrom)) files[Normalize(path)] = content;
        foreach (var (path, content) in spec.Files ?? []) files[Normalize(path)] = Utf8(content);
        foreach (var blob in spec.Blobs ?? []) files[Normalize(blob.Path)] = Blob(blob);
        return files;
    }

    private static Dictionary<string, byte[]?> BranchContent(SimModel model, SeedRepo spec, string branch)
    {
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        { [$"{spec.HistoryDir}/branch.txt"] = Utf8(branch) };
        if (spec.BranchFilesFrom?.GetValueOrDefault(branch) is { } folder)
            foreach (var (path, content) in model.FilesFor(folder)) files[Normalize(path)] = content;
        if (spec.BranchFiles?.GetValueOrDefault(branch) is { } inline)
            foreach (var (path, content) in inline) files[Normalize(path)] = Utf8(content);
        return files;
    }

    public static async Task BuildAsync(SimModel model, SimRepo repo)
    {
        var spec = repo.Spec;
        Directory.CreateDirectory(Path.GetDirectoryName(repo.BarePath)!);
        await Git.MustAsync(null, null, "init", "--bare", "--quiet", "-b", spec.DefaultBranch, repo.BarePath);
        // Background gc would race with the repository being wiped by a reset or reseed.
        await File.AppendAllTextAsync(Path.Combine(repo.BarePath, "config"), "[receive]\n\tautogc = false\n[gc]\n\tauto = 0\n");
        if (spec.Empty || spec.Commits < 1) return;

        var authors = Authors(model, spec);
        var real = RealFiles(model, spec);
        await using var import = FastImport.Start(repo.BarePath);
        var mark = 0;
        string? previous = null;
        for (var i = 1; i <= spec.Commits; i++)
        {
            var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            if (!real.ContainsKey("README.md")) changes["README.md"] = Utf8($"# {spec.Name}\nrevision {i}\n");
            changes[$"{spec.HistoryDir}/file{i}.txt"] = Utf8($"{spec.Name} content {i}\n");
            if (i == 1) foreach (var (path, content) in real) changes[path] = content;
            import.Commit(repo.DefaultRef, ++mark, authors[(i - 1) % authors.Count],
                (spec.Start ?? Epoch).AddDays(i - 1).ToUnixTimeSeconds(), $"Commit {i} of {spec.Name}", previous, changes);
            previous = $":{mark}";
        }
        var tip = previous!;

        // Seeds without `start` keep the original date scheme (and therefore their historical SHAs).
        long SideDate(int offset, int legacyDay) => spec.Start is { } start
            ? start.AddDays(spec.Commits + offset).ToUnixTimeSeconds() : Day(legacyDay);
        var day = spec.Commits + 1;
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in (spec.Branches ?? []).Concat(spec.BranchFiles?.Keys ?? Enumerable.Empty<string>()).Concat(spec.BranchFilesFrom?.Keys ?? Enumerable.Empty<string>()))
        {
            if (!created.Add(branch)) continue;
            import.Commit("refs/heads/" + branch, ++mark, authors[(spec.Commits + created.Count - 1) % authors.Count],
                SideDate(created.Count, day++ % 28 + 1), $"Work on {branch}", tip, BranchContent(model, spec, branch));
        }
        foreach (var tag in spec.Tags ?? []) import.Reset("refs/tags/" + tag, tip);
        foreach (var tag in spec.AnnotatedTags ?? [])
            import.Tag(tag, tip, authors[0], SideDate(created.Count + 1, day % 28 + 1), $"Release {tag}");
        for (var i = 0; i < spec.ExtraBranches; i++) import.Reset($"refs/heads/bulk/branch-{i:0000}", tip);
        for (var i = 0; i < spec.ExtraTags; i++) import.Reset($"refs/tags/bulk-tag-{i:0000}", tip);
        await import.FinishAsync();
    }

    // Counter-mode SHA-256: reproducible, incompressible bytes.
    public static byte[] Blob(SeedBlob blob)
    {
        var size = Math.Max(blob.Size, 0);
        var key = Utf8($"adosim-blob:{blob.Path}:{size}:{blob.Kind}");
        var raw = new byte[size];
        for (int offset = 0, counter = 0; offset < size; counter++)
        {
            var block = SHA256.HashData([.. key, .. BitConverter.GetBytes(counter)]);
            var take = Math.Min(block.Length, size - offset);
            Array.Copy(block, 0, raw, offset, take);
            offset += take;
        }
        switch (blob.Kind.ToLowerInvariant())
        {
            case "text":
                for (var i = 0; i < size; i++) raw[i] = i % 80 == 79 ? (byte)'\n' : (byte)"0123456789abcdef"[raw[i] & 15];
                break;
            case "png":
                byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                Array.Copy(signature, raw, Math.Min(signature.Length, size));
                break;
            default:
                if (size > 0) raw[0] = 0;
                break;
        }
        return raw;
    }

    // Used by the write API: commits strictly after everything already in the repository.
    private static async Task<long> NextTimestampAsync(SimRepo repo)
    {
        var (exit, output, _) = await Git.RunAsync(repo.BarePath, null, "for-each-ref", "--format=%(creatordate:unix)", "refs/heads", "refs/tags");
        var latest = exit == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => long.TryParse(l, out var v) ? v : 0).DefaultIfEmpty(0).Max()
            : 0;
        return Math.Max(latest, ApiFloor) + 3600;
    }

    private static async Task<string?> ResolveCommitAsync(SimRepo repo, string name)
    {
        if (name.StartsWith('-')) return null;
        foreach (var candidate in new[] { "refs/heads/" + name, "refs/tags/" + name, name })
            if (await Git.RevParseAsync(repo, candidate + "^{commit}") is { Length: 40 } sha) return sha;
        return null;
    }

    private static async Task RequireRefFormatAsync(string reference)
    {
        var (exit, _, _) = await Git.RunAsync(null, null, "check-ref-format", reference);
        if (exit != 0) throw new SimRequestException(400, $"'{reference}' is not a valid ref name.");
    }

    private static string CleanPath(string path)
    {
        var clean = Normalize(path);
        var parts = clean.Split('/');
        if (clean.Length == 0 || parts.Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new SimRequestException(400, $"Invalid path '{path}'.");
        return clean;
    }

    public static async Task<string> CommitAsync(SimRepo repo, CommitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Branch)) throw new SimRequestException(400, "branch is required.");
        if (string.IsNullOrWhiteSpace(request.Message)) throw new SimRequestException(400, "message is required.");
        var branch = request.Branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? request.Branch["refs/heads/".Length..] : request.Branch;
        var reference = "refs/heads/" + branch;
        await RequireRefFormatAsync(reference);

        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var path in request.Delete ?? []) changes[CleanPath(path)] = null;
        foreach (var (path, content) in request.Files ?? []) changes[CleanPath(path)] = Utf8(content);

        var parent = await Git.RevParseAsync(repo, reference + "^{commit}");
        if (parent is null)
        {
            if (!string.IsNullOrWhiteSpace(request.CreateBranchFrom))
                parent = await ResolveCommitAsync(repo, request.CreateBranchFrom)
                    ?? throw new SimRequestException(400, $"createBranchFrom '{request.CreateBranchFrom}' does not exist.");
            else if ((await Git.RefsAsync(repo)).Count > 0)
                throw new SimRequestException(400, $"Branch '{branch}' does not exist; pass createBranchFrom to create it.");
        }
        string? tagRef = null;
        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            tagRef = "refs/tags/" + request.Tag;
            await RequireRefFormatAsync(tagRef);
            if (await Git.RevParseAsync(repo, tagRef) is not null) throw new SimRequestException(409, $"Tag '{request.Tag}' already exists.");
        }

        var timestamp = await NextTimestampAsync(repo);
        await using (var import = FastImport.Start(repo.BarePath))
        {
            import.Commit(reference, 1, GitPerson.Sim, timestamp, request.Message, parent, changes);
            if (tagRef is not null) import.Reset(tagRef, ":1");
            await import.FinishAsync();
        }
        return (await Git.RevParseAsync(repo, reference))!;
    }
}
