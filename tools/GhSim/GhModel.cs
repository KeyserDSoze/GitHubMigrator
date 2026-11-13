using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhSim;

public sealed record GhSeed(
    List<SeedUser>? Users = null, List<SeedOrg>? Orgs = null, List<SeedRepo>? Repos = null, List<SeedApp>? Apps = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A path, or the built-in "default" profile when null.
    public static GhSeed Load(string? path)
    {
        using Stream stream = string.IsNullOrWhiteSpace(path) || path == "default"
            ? typeof(GhSeed).Assembly.GetManifestResourceStream("default-gh-seed.json")!
            : File.OpenRead(path);
        return JsonSerializer.Deserialize<GhSeed>(stream, Json)!;
    }

    public string Hash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));
}

// WorkflowScope is the classic-token `workflow` scope: without it a token cannot create or update files under .github/workflows.
public sealed record SeedUser(string Login, string? Token = null, bool ReadOnly = false, bool WorkflowScope = true);
public sealed record SeedOrg(string Login, bool MembersCanCreatePrivateRepositories = true, Dictionary<string, string>? Members = null);
public sealed record SeedRuleset(string Name, string Enforcement, string Target, List<string> Include, List<string> Rules);
public sealed record SeedRepo(
    string Org, string Name, bool Private = true, string DefaultBranch = "main", bool Archived = false,
    bool Disabled = false, string? Description = null, Dictionary<string, string>? Files = null,
    List<SeedRuleset>? Rulesets = null, Dictionary<string, string>? Variables = null, List<string>? Secrets = null);
// PullRequests: none|read|write. Workflows: none|write (GitHub has no read level for it). Both default to none: a installation is granted only what it is given.
public sealed record SeedInstallation(long Id, string Org, string RepositorySelection = "selected", string Contents = "write",
    string PullRequests = "none", string Workflows = "none");
public sealed record SeedApp(long Id, string Slug, string? PublicKeyPem = null, List<SeedInstallation>? Installations = null);

public sealed class GhUser
{
    public string Login { get; set; } = "";
    public long Id { get; set; }
    public string? Token { get; set; }
    public bool ReadOnly { get; set; }
    public bool WorkflowScope { get; set; } = true;
}

public sealed class GhOrg
{
    public string Login { get; set; } = "";
    public long Id { get; set; }
    public bool MembersCanCreatePrivateRepositories { get; set; } = true;
    public Dictionary<string, string> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GhRuleset
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Enforcement { get; set; } = "active";
    public string Target { get; set; } = "branch";
    public List<string> Include { get; set; } = [];
    public List<string> Rules { get; set; } = [];
}

public sealed class GhIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public List<string> Labels { get; set; } = [];
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<GhComment> Comments { get; set; } = [];
}

public sealed class GhComment
{
    public long Id { get; set; }
    public string Body { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GhPull
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public bool Draft { get; set; }
    public string HeadRef { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string BaseRef { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public bool Merged { get; set; }
    public string? MergeCommitSha { get; set; }
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class GhEnvironment
{
    public string Name { get; set; } = "";
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int ReviewerCount { get; set; }
    public int WaitTimer { get; set; }
}

public sealed class GhRepo
{
    public long Id { get; set; }
    public string Org { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Private { get; set; } = true;
    public string DefaultBranch { get; set; } = "main";
    public bool Archived { get; set; }
    public bool Disabled { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset PushedAt { get; set; }
    public string SecretKeyId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public List<GhRuleset> Rulesets { get; set; } = [];
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GhEnvironment> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GhIssue> Issues { get; set; } = [];
    public List<GhPull> Pulls { get; set; } = [];
    public int NextNumber { get; set; } = 1;
    public long NextCommentId { get; set; } = 1;
    public string FullName => Org + "/" + Name;
}

public sealed class GhInstallation
{
    public long Id { get; set; }
    public string Org { get; set; } = "";
    public string RepositorySelection { get; set; } = "selected";
    public string Contents { get; set; } = "write";
    public string PullRequests { get; set; } = "none";
    public string Workflows { get; set; } = "none";
}

public sealed class GhApp
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string? PublicKeyPem { get; set; }
    public List<GhInstallation> Installations { get; set; } = [];
}

public sealed class GhState
{
    public string SeedHash { get; set; } = "";
    public long NextRepoId { get; set; } = 1_000_000;
    public long NextAccountId { get; set; } = 5_000;
    public long NextRulesetId { get; set; } = 100;
    public long NextEnvironmentId { get; set; } = 7_000;
    public List<GhUser> Users { get; set; } = [];
    public List<GhOrg> Orgs { get; set; } = [];
    public List<GhRepo> Repos { get; set; } = [];
    public List<GhApp> Apps { get; set; } = [];
}

// Single-writer in-memory state persisted as JSON next to the bare repositories.
// Callers take Lock for every read-modify-write so persisted state never tears.
public sealed class GhStore(string dataDir)
{
    public object Lock { get; } = new();
    public GhState State { get; private set; } = new();
    private string StatePath => Path.Combine(dataDir, "state.json");
    public string ReposDir => Path.Combine(dataDir, "repos");

    public string BarePath(GhRepo repo) => Path.Combine(ReposDir, repo.Id + ".git");

    public bool TryLoad(string seedHash)
    {
        if (!File.Exists(StatePath)) return false;
        try
        {
            var state = JsonSerializer.Deserialize<GhState>(File.ReadAllText(StatePath), GhSeed.Json);
            if (state is null || state.SeedHash != seedHash ||
                state.Repos.Any(r => !Directory.Exists(BarePath(r)))) return false;
            State = state;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public void Reset(string seedHash)
    {
        lock (Lock)
        {
            State = new GhState { SeedHash = seedHash };
            if (Directory.Exists(ReposDir)) ForceDelete(ReposDir);
            Directory.CreateDirectory(ReposDir);
        }
    }

    public void Save()
    {
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State, GhSeed.Json));
        File.Move(temp, StatePath, true);
    }

    public GhOrg? Org(string login) => State.Orgs.FirstOrDefault(o => o.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
    public GhUser? User(string login) => State.Users.FirstOrDefault(u => u.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
    public GhRepo? Repo(string org, string name) => State.Repos.FirstOrDefault(r =>
        r.Org.Equals(org, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public GhRepo? Repo(long id) => State.Repos.FirstOrDefault(r => r.Id == id);

    public static void ForceDelete(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
        catch (IOException) { /* best effort on temp data */ }
    }

    public static string Deterministic(string value, int bytes)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash.AsSpan(0, bytes));
    }
}
