using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdoSim;

public sealed record SeedSpec(
    string Collection, List<SeedUser> Users, List<SeedGroup> Groups, List<SeedProject> Projects,
    List<SeedVariableGroup>? VariableGroups = null, List<SeedServiceEndpoint>? ServiceEndpoints = null,
    List<SeedWorkItem>? WorkItems = null)
{
    public static SeedSpec Load(string? path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using Stream stream = path is null
            ? typeof(SeedSpec).Assembly.GetManifestResourceStream("default-seed.json")!
            : File.OpenRead(path);
        return JsonSerializer.Deserialize<SeedSpec>(stream, options)!;
    }
}

public sealed record SeedUser(string Name, string Display, string? Mail);
public sealed record SeedGroup(string Name, List<string> Members);
public sealed record SeedAce(string Principal, long Allow, long Deny);
public sealed record SeedRepo(
    string Name, string DefaultBranch = "main", int Commits = 3, List<string>? Branches = null,
    List<string>? Tags = null, List<string>? AnnotatedTags = null, bool Empty = false,
    bool Disabled = false, int ExtraBranches = 0, int ExtraTags = 0,
    Dictionary<string, string>? Files = null, List<SeedAce>? Acl = null, bool InheritPermissions = true);
public sealed record SeedVariable(string Name, bool Secret);
public sealed record SeedPipeline(int Id, string Name, string Repo, string Path,
    List<SeedVariable>? Variables = null, List<int>? VariableGroups = null);
public sealed record SeedProject(
    string Name, string SourceControl, List<SeedRepo>? Repos = null, List<string>? TfvcBranches = null,
    List<SeedPipeline>? Pipelines = null, List<SeedAce>? Acl = null);
public sealed record SeedVariableGroup(int Id, string Name, List<SeedVariable> Variables);
public sealed record SeedServiceEndpoint(string Name, string Type);
public sealed record SeedLink(string Kind, int? To = null, string? Repo = null, string? Ref = null);
public sealed record SeedWorkItem(int Id, string Project, string Type, string Title, string State,
    string? Description, List<SeedLink>? Links = null);

public static class Ids
{
    // Deterministic ids keep expected results and URLs stable across restarts.
    public static string Guid(string name) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToString();
}

public sealed class SimIdentity(string name, string display, string? mail, bool isContainer)
{
    public string Name { get; } = name;
    public string Display { get; } = display;
    public string? Mail { get; } = mail;
    public bool IsContainer { get; } = isContainer;
    public string Id { get; } = Ids.Guid("identity:" + name);
    public string Descriptor { get; } = isContainer
        ? "Microsoft.TeamFoundation.Identity;S-1-9-" + Ids.Guid("group:" + name)
        : "Microsoft.IdentityModel.Claims.ClaimsIdentity;SIM\\" + name;
    public List<string> MemberNames { get; } = [];
}

public sealed class SimRepo(SeedRepo spec, string projectId, string dataDir)
{
    public SeedRepo Spec { get; } = spec;
    public string ProjectId { get; } = projectId;
    public string Name => Spec.Name;
    public string Id { get; } = Ids.Guid($"repo:{projectId}:{spec.Name}");
    public string BarePath { get; } = Path.Combine(dataDir, "repos", Ids.Guid($"repo:{projectId}:{spec.Name}") + ".git");
}

public sealed class SimProject(SeedProject spec, string dataDir)
{
    public SeedProject Spec { get; } = spec;
    public string Name => Spec.Name;
    public string Id { get; } = Ids.Guid("project:" + spec.Name);
    public List<SimRepo> Repos { get; } = (spec.Repos ?? []).Select(r => new SimRepo(r, Ids.Guid("project:" + spec.Name), dataDir)).ToList();
    public bool IsTfvc => string.Equals(Spec.SourceControl, "Tfvc", StringComparison.OrdinalIgnoreCase);
}

public sealed class SimModel
{
    public SeedSpec Seed { get; }
    public List<SimProject> Projects { get; }
    public List<SimIdentity> Identities { get; }

    public SimModel(SeedSpec seed, string dataDir)
    {
        Seed = seed;
        Projects = seed.Projects.Select(p => new SimProject(p, dataDir)).ToList();
        var users = seed.Users.Select(u => new SimIdentity(u.Name, u.Display, u.Mail, false)).ToList();
        var groups = seed.Groups.Select(g => new SimIdentity(g.Name, g.Name, null, true)).ToList();
        foreach (var (group, spec) in groups.Zip(seed.Groups)) group.MemberNames.AddRange(spec.Members);
        Identities = [.. users, .. groups];
    }

    public SimIdentity? FindByName(string name) => Identities.FirstOrDefault(i => i.Name == name);
    public SimIdentity? FindByDescriptor(string d) => Identities.FirstOrDefault(i => i.Descriptor == d);

    // Orphan principals (unknown to the identity service) keep their raw descriptor.
    public string DescriptorFor(string principal) => FindByName(principal)?.Descriptor ?? principal;

    public int Count(Func<SimRepo, int> perRepo) => Projects.Sum(p => p.Repos.Sum(perRepo));
}
