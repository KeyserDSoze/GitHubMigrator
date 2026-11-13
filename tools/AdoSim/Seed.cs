using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdoSim;

public sealed record SeedUser(string Name, string Display, string? Mail);
public sealed record SeedGroup(string Name, List<string> Members);
public sealed record SeedAce(string Principal, long Allow, long Deny);
// Deterministic pseudo-random content, so large/binary files never have to live in the source tree.
public sealed record SeedBlob(string Path, int Size, string Kind = "binary");

public sealed record SeedRepo(
    string Name, string DefaultBranch = "main", int Commits = 3, List<string>? Branches = null,
    List<string>? Tags = null, List<string>? AnnotatedTags = null, bool Empty = false,
    bool Disabled = false, int ExtraBranches = 0, int ExtraTags = 0,
    Dictionary<string, string>? Files = null, List<SeedAce>? Acl = null, bool InheritPermissions = true,
    string? FilesFrom = null, Dictionary<string, Dictionary<string, string>>? BranchFiles = null,
    Dictionary<string, string>? BranchFilesFrom = null, List<SeedBlob>? Blobs = null,
    List<string>? Authors = null, string HistoryDir = "src", DateTimeOffset? Start = null);

public sealed record SeedVariable(string Name, bool Secret, string? Value = null);

public sealed record SeedClassicStep(string Task, string DisplayName, Dictionary<string, string>? Inputs = null,
    string? Condition = null, bool Enabled = true, bool ContinueOnError = false);
public sealed record SeedClassicPhase(string Name, List<SeedClassicStep> Steps);
public sealed record SeedClassic(List<SeedClassicPhase> Phases, string Queue = "Default", string? AgentSpec = null,
    string? BuildNumberFormat = null, List<string>? CiBranches = null, bool Batch = false, int? ScheduleHour = null);

// A pipeline is YAML unless it carries a classic (designer) definition.
public sealed record SeedPipeline(int Id, string Name, string Repo, string? Path = null,
    List<SeedVariable>? Variables = null, List<int>? VariableGroups = null,
    string? Folder = null, SeedClassic? Classic = null);

public sealed record SeedProject(
    string Name, string SourceControl, List<SeedRepo>? Repos = null, List<string>? TfvcBranches = null,
    List<SeedPipeline>? Pipelines = null, List<SeedAce>? Acl = null);

// Projects == null means visible in every project (the default seed predates project scoping).
public sealed record SeedVariableGroup(int Id, string Name, List<SeedVariable> Variables, string? Type = null,
    string? Description = null, List<string>? Projects = null, string? KeyVault = null,
    string? KeyVaultEndpoint = null, string? CreatedBy = null);

public sealed record SeedServiceEndpoint(string Name, string Type, string? Id = null, string? Url = null,
    string? Scheme = null, bool IsShared = false, bool IsReady = true, string? CreatedBy = null,
    string? Description = null, List<string>? Projects = null, Dictionary<string, string>? Data = null,
    Dictionary<string, string>? Parameters = null);

public sealed record SeedCheck(string Type, List<string>? Approvers = null, string? Order = null,
    int? MinApprovers = null, string? Branches = null, string? Instructions = null, int Timeout = 43200);
public sealed record SeedEnvironment(int Id, string Name, string Project, string? Description = null,
    List<SeedCheck>? Checks = null);

// Type: minReviewers | build | workItemLinking | commentResolution | requiredReviewers. Repo null = whole project.
public sealed record SeedPolicy(int Id, string Project, string Type, string? Repo = null, string? Branch = null,
    bool Enabled = true, bool Blocking = true, int? Min = null, int? Pipeline = null,
    List<string>? Reviewers = null, string? CreatedBy = null);

// Event: git.push | git.pullrequest.created | build.complete. Consumer: webhook | slack | teams.
public sealed record SeedHook(string Name, string Event, string Consumer, string? Project = null,
    string? Repo = null, string? Branch = null, string? Url = null, bool Enabled = true, string? CreatedBy = null);

public sealed record SeedReviewer(string User, int Vote = 0, bool Required = false);
public sealed record SeedPullRequest(int Id, string Project, string Repo, string Title, string Status,
    string Source, string Target, string CreatedBy, DateTimeOffset Created, string? Description = null,
    DateTimeOffset? Closed = null, bool Draft = false, List<SeedReviewer>? Reviewers = null,
    List<int>? WorkItems = null, string? ClosedBy = null, List<string>? Labels = null);

// Version is a rev expression relative to the pipeline repository (e.g. "main~2"); default is the branch tip.
public sealed record SeedRun(int Id, int Pipeline, string Result, string Branch = "main",
    DateTimeOffset? Started = null, int Minutes = 5, string? Version = null, string? Name = null);

public sealed record SeedLink(string Kind, int? To = null, string? Repo = null, string? Ref = null);
public sealed record SeedWorkItem(int Id, string Project, string Type, string Title, string State,
    string? Description, List<SeedLink>? Links = null, string? AssignedTo = null, string? Tags = null);

public sealed record SeedGenerate(int Projects = 0, int ReposPerProject = 1, int PipelinesPerRepo = 1,
    int CommitsPerRepo = 3)
{
    // "projects,reposPerProject,pipelinesPerRepo[,commitsPerRepo]"
    public static SeedGenerate Parse(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4 || parts.Any(p => !int.TryParse(p, out var n) || n < 0))
            throw new ArgumentException($"ADO_SIM_BULK must be 'projects,reposPerProject,pipelinesPerRepo[,commitsPerRepo]' (got '{text}').");
        int At(int i, int fallback) => i < parts.Length ? int.Parse(parts[i]) : fallback;
        return new SeedGenerate(At(0, 0), At(1, 1), At(2, 1), At(3, 3));
    }
}

public sealed record SeedSpec(
    string Collection, List<SeedUser> Users, List<SeedGroup> Groups, List<SeedProject> Projects,
    List<SeedVariableGroup>? VariableGroups = null, List<SeedServiceEndpoint>? ServiceEndpoints = null,
    List<SeedWorkItem>? WorkItems = null, List<SeedEnvironment>? Environments = null,
    List<SeedPolicy>? Policies = null, List<SeedHook>? Hooks = null,
    List<SeedPullRequest>? PullRequests = null, List<SeedRun>? Runs = null, SeedGenerate? Generate = null)
{
    public static readonly string[] Profiles = ["default", "orion"];
    public const int BulkPipelineIdBase = 10000;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Where <filesFrom> folders are looked up before the embedded resources (set for seeds loaded from disk).
    [JsonIgnore] public string? FilesRoot { get; init; }

    // source: null/empty/"default", a built-in profile name, or a path to a seed file.
    // bulk: "projects,reposPerProject,pipelinesPerRepo[,commitsPerRepo]" (ADO_SIM_BULK), overrides `generate`.
    public static SeedSpec Load(string? source, string? bulk = null)
    {
        var name = string.IsNullOrWhiteSpace(source) ? "default" : source.Trim();
        SeedSpec seed;
        if (Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = typeof(SeedSpec).Assembly.GetManifestResourceStream(name.ToLowerInvariant() + "-seed.json")
                ?? throw new InvalidOperationException($"Built-in seed '{name}' is missing from the assembly.");
            seed = JsonSerializer.Deserialize<SeedSpec>(stream, JsonOptions)!;
        }
        else if (File.Exists(name))
        {
            using var stream = File.OpenRead(name);
            seed = JsonSerializer.Deserialize<SeedSpec>(stream, JsonOptions)! with
            { FilesRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(name))!, "files") };
        }
        else throw new FileNotFoundException(
            $"ADO_SIM_SEED '{name}' is neither a built-in profile ({string.Join(", ", Profiles)}) nor an existing file.");
        if (!string.IsNullOrWhiteSpace(bulk)) seed = seed with { Generate = SeedGenerate.Parse(bulk) };
        return seed.Expand();
    }

    // Deterministic expansion of `generate` into plain projects/repos/pipelines.
    public SeedSpec Expand()
    {
        if (Generate is not { Projects: > 0 } g) return this;
        var repos = Math.Max(g.ReposPerProject, 0);
        if ((long)g.Projects * repos > 20_000) throw new ArgumentException("generate would create more than 20000 repositories.");
        var projectWidth = Math.Max(3, g.Projects.ToString().Length);
        var repoWidth = Math.Max(2, repos.ToString().Length);
        var pipelineId = Math.Max(BulkPipelineIdBase, (Projects.SelectMany(p => p.Pipelines ?? []).Select(x => x.Id).DefaultIfEmpty(0).Max()));
        var projects = new List<SeedProject>(Projects);
        for (var p = 1; p <= g.Projects; p++)
        {
            var label = $"Bulk {p.ToString().PadLeft(projectWidth, '0')}";
            var repoSpecs = new List<SeedRepo>();
            var pipelines = new List<SeedPipeline>();
            for (var r = 1; r <= repos; r++)
            {
                var repoName = $"bulk-{p.ToString().PadLeft(projectWidth, '0')}-repo-{r.ToString().PadLeft(repoWidth, '0')}";
                var files = new Dictionary<string, string>();
                for (var k = 1; k <= Math.Max(g.PipelinesPerRepo, 0); k++)
                {
                    var path = $"pipelines/ci-{k}.yml";
                    files[path] = $"trigger:\n  branches:\n    include:\n    - main\npool:\n  vmImage: ubuntu-latest\nsteps:\n" +
                                  $"- script: echo \"{repoName} pipeline {k}\"\n  displayName: Build {repoName} {k}\n";
                    pipelines.Add(new SeedPipeline(++pipelineId, $"{label} {repoName} CI {k}", repoName, "/" + path));
                }
                repoSpecs.Add(new SeedRepo(repoName, Commits: Math.Max(g.CommitsPerRepo, 1), Files: files));
            }
            projects.Add(new SeedProject(label, "Git", repoSpecs, Pipelines: pipelines));
        }
        return this with { Projects = projects, Generate = null };
    }

    // Hash of everything that decides repository content: the effective seed (after expansion), the
    // bytes of every referenced seed-file folder and the builder version.
    public string ComputeHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
        Add($"adosim-seed/builder-{RepoBuilder.Version}\n");
        Add(JsonSerializer.Serialize(this, JsonOptions));
        var folders = Projects.SelectMany(p => p.Repos ?? [])
            .SelectMany(r => new[] { r.FilesFrom }.Concat(r.BranchFilesFrom?.Values ?? Enumerable.Empty<string>()))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            Add($"\nfolder:{folder}\n");
            foreach (var (path, content) in SeedFiles.Read(folder, FilesRoot))
            {
                Add($"file:{path}:{content.Length}:");
                hash.AppendData(SHA256.HashData(content));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

// Files shipped next to the seed (tools/AdoSim/seed/files/<folder>/**), embedded in the assembly.
public static class SeedFiles
{
    private const string Prefix = "seed/files/";

    public static List<(string Path, byte[] Content)> Read(string folder, string? filesRoot = null)
    {
        var result = new List<(string, byte[])>();
        var disk = filesRoot is null ? null : Path.Combine(filesRoot, folder);
        if (disk is not null && Directory.Exists(disk))
        {
            foreach (var file in Directory.EnumerateFiles(disk, "*", SearchOption.AllDirectories))
                result.Add((Path.GetRelativePath(disk, file).Replace('\\', '/'), Normalize(File.ReadAllBytes(file))));
        }
        else
        {
            var assembly = typeof(SeedFiles).Assembly;
            var root = Prefix + folder.Trim('/') + "/";
            // MSBuild writes RecursiveDir with the separator of the build machine.
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                result.Add((resource.Replace('\\', '/')[root.Length..], Normalize(buffer.ToArray())));
            }
        }
        if (result.Count == 0) throw new InvalidOperationException($"Seed files folder '{folder}' is empty or missing.");
        return result.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
    }

    // Repositories always receive LF text, whatever line endings a checkout produced.
    private static byte[] Normalize(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0 || Array.IndexOf(bytes, (byte)'\r') < 0) return bytes;
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            if (!(bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')) output.Add(bytes[i]);
        return output.ToArray();
    }
}

public static class Ids
{
    // Deterministic ids keep expected results and URLs stable across restarts.
    public static string Guid(string name) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToString();
}
