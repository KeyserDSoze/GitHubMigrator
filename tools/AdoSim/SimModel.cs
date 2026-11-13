using System.Collections.Concurrent;

namespace AdoSim;

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

public sealed class SimRepo(SeedRepo spec, string projectId, string projectName, string dataDir)
{
    public SeedRepo Spec { get; } = spec;
    public string ProjectId { get; } = projectId;
    public string ProjectName { get; } = projectName;
    public string Name => Spec.Name;
    public string Id { get; } = Ids.Guid($"repo:{projectId}:{spec.Name}");
    public string BarePath { get; } = Path.Combine(dataDir, "repos", Ids.Guid($"repo:{projectId}:{spec.Name}") + ".git");
    public string DefaultRef => "refs/heads/" + Spec.DefaultBranch;
}

public sealed class SimProject(SeedProject spec, string dataDir)
{
    public SeedProject Spec { get; } = spec;
    public string Name => Spec.Name;
    public string Id { get; } = Ids.Guid("project:" + spec.Name);
    public List<SimRepo> Repos { get; } = (spec.Repos ?? [])
        .Select(r => new SimRepo(r, Ids.Guid("project:" + spec.Name), spec.Name, dataDir)).ToList();
    public bool IsTfvc => string.Equals(Spec.SourceControl, "Tfvc", StringComparison.OrdinalIgnoreCase);
}

public sealed class SimModel
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new();

    public SeedSpec Seed { get; }
    public string DataDir { get; }
    public List<SimProject> Projects { get; }
    public List<SimIdentity> Identities { get; }
    public IEnumerable<SimRepo> Repos => Projects.SelectMany(p => p.Repos);
    public IEnumerable<(SimProject Project, SeedPipeline Pipeline)> Pipelines =>
        Projects.SelectMany(p => (p.Spec.Pipelines ?? []).Select(x => (p, x)));

    public SimModel(SeedSpec seed, string dataDir)
    {
        Seed = seed;
        DataDir = dataDir;
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

    public SimProject? ProjectByName(string? name) => Projects.FirstOrDefault(p => p.Name == name);
    public SimRepo? RepoByName(string? project, string? repo) =>
        ProjectByName(project)?.Repos.FirstOrDefault(r => r.Name == repo);

    // The seed predates project scoping: a null list means "visible everywhere".
    public static bool Visible(List<string>? projects, string project) => projects is null || projects.Contains(project);

    public Task<IDisposable> LockRepoAsync(SimRepo repo) => LockAsync(_repoLocks.GetOrAdd(repo.Id, _ => new SemaphoreSlim(1, 1)));

    private static async Task<IDisposable> LockAsync(SemaphoreSlim gate)
    {
        await gate.WaitAsync();
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    private readonly ConcurrentDictionary<string, List<(string Path, byte[] Content)>> _folders = new();
    public List<(string Path, byte[] Content)> FilesFor(string folder) =>
        _folders.GetOrAdd(folder, f => SeedFiles.Read(f, Seed.FilesRoot));
}
