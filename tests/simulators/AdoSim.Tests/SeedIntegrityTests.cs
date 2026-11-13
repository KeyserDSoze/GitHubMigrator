namespace AdoSim.Tests;

// Cross-checks the orion seed data against itself, so a typo cannot create a dangling reference.
public sealed class SeedIntegrityTests
{
    private static readonly SeedSpec Seed = SeedSpec.Load("orion");

    private static bool BranchExists(SeedRepo repo, string branch) =>
        branch == repo.DefaultBranch || (repo.Branches ?? []).Contains(branch) ||
        (repo.BranchFiles?.ContainsKey(branch) ?? false) || (repo.BranchFilesFrom?.ContainsKey(branch) ?? false);

    private static string Short(string reference) => reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    private static SeedRepo RepoOf(string project, string repo) => Seed.Projects.Single(p => p.Name == project).Repos!.Single(r => r.Name == repo);

    [Fact]
    public void The_estate_has_the_requested_size_and_variety()
    {
        Assert.Equal(["Orion Commerce", "Orion Platform", "Orion Sandbox"], Seed.Projects.Select(p => p.Name));
        Assert.All(Seed.Projects, p => Assert.Equal("Git", p.SourceControl));
        Assert.Equal(10, Seed.Users.Count);
        Assert.Contains(Seed.Users, u => u.Name == "svc-build" && u.Mail is null);
        Assert.Equal(Seed.Users.Select(u => u.Name).Distinct().Count(), Seed.Users.Count);
        Assert.True(Seed.ServiceEndpoints!.Count >= 9);
        Assert.True(Seed.VariableGroups!.Count >= 4);
        Assert.Contains(Seed.VariableGroups, g => g.KeyVault is not null);
        Assert.True(Seed.Hooks!.Count >= 3);
        Assert.True(Seed.PullRequests!.Count >= 6);
        Assert.Superset(new HashSet<string> { "active", "completed", "abandoned" }, Seed.PullRequests.Select(p => p.Status).ToHashSet());
        Assert.Superset(new HashSet<string> { "dev", "staging", "prod" }, Seed.Environments!.Select(e => e.Name).ToHashSet());
        Assert.Superset(new HashSet<string> { "minReviewers", "build", "workItemLinking", "commentResolution" }, Seed.Policies!.Select(p => p.Type).ToHashSet());
        Assert.Superset(new HashSet<string> { "succeeded", "failed", "canceled" }, Seed.Runs!.Select(r => r.Result).ToHashSet());
        var repos = Seed.Projects.SelectMany(p => p.Repos!).ToList();
        Assert.Contains(repos, r => r.Empty);
        Assert.Contains(repos, r => r.Disabled);
        Assert.Contains(repos, r => r.Name.Contains(' ') || r.Name.Contains('.'));
        Assert.Contains(repos, r => r.Commits >= 40);
        Assert.Contains(repos, r => r.Blobs?.Any(b => b.Size > 1_000_000) == true && r.Blobs.Any(b => b.Kind != "text"));
        Assert.Contains(repos, r => r.DefaultBranch.Length > 60 && (r.Branches ?? []).Any(b => b.Any(c => c > 127)));
        Assert.Contains(repos, r => r.BranchFiles is not null || r.BranchFilesFrom is not null);
        Assert.Contains(Seed.Projects.SelectMany(p => p.Repos!), r => r.Acl?.Any(a => a.Deny != 0) == true);
        Assert.Contains(Seed.Projects.SelectMany(p => p.Repos!), r => !r.InheritPermissions);
        Assert.Equal(1, Seed.Projects.SelectMany(p => p.Pipelines!).Count(p => p.Classic is not null));
    }

    [Fact]
    public void Identity_references_resolve()
    {
        var known = Seed.Users.Select(u => u.Name).Concat(Seed.Groups.Select(g => g.Name)).ToHashSet();
        foreach (var group in Seed.Groups) Assert.All(group.Members, m => Assert.Contains(m, known));
        var people = new List<string?>();
        people.AddRange(Seed.PullRequests!.SelectMany(p => new[] { p.CreatedBy, p.ClosedBy }.Concat(p.Reviewers?.Select(r => r.User) ?? [])));
        people.AddRange(Seed.ServiceEndpoints!.Select(e => e.CreatedBy));
        people.AddRange(Seed.VariableGroups!.Select(g => g.CreatedBy));
        people.AddRange(Seed.Hooks!.Select(h => h.CreatedBy));
        people.AddRange(Seed.Policies!.Select(p => p.CreatedBy));
        people.AddRange(Seed.Policies!.SelectMany(p => p.Reviewers ?? []));
        people.AddRange(Seed.Environments!.SelectMany(e => e.Checks ?? []).SelectMany(c => c.Approvers ?? []));
        people.AddRange(Seed.WorkItems!.Select(w => w.AssignedTo));
        people.AddRange(Seed.Projects.SelectMany(p => p.Repos!).SelectMany(r => r.Authors ?? []));
        Assert.All(people.OfType<string>(), person => Assert.Contains(person, known));
        foreach (var ace in Seed.Projects.SelectMany(p => (p.Acl ?? []).Concat(p.Repos!.SelectMany(r => r.Acl ?? []))))
            Assert.True(known.Contains(ace.Principal) || ace.Principal.StartsWith("S-1-9-orphan", StringComparison.Ordinal), ace.Principal);
    }

    [Fact]
    public void Ids_are_unique_and_projects_exist()
    {
        foreach (var ids in new[]
        {
            Seed.Projects.SelectMany(p => p.Pipelines!).Select(p => p.Id), Seed.VariableGroups!.Select(g => g.Id),
            Seed.Environments!.Select(e => e.Id), Seed.Policies!.Select(p => p.Id), Seed.PullRequests!.Select(p => p.Id),
            Seed.Runs!.Select(r => r.Id), Seed.WorkItems!.Select(w => w.Id)
        })
            Assert.Equal(ids.Count(), ids.Distinct().Count());
        Assert.Equal(Seed.ServiceEndpoints!.Count, Seed.ServiceEndpoints.Select(e => e.Name).Distinct().Count());
        var projects = Seed.Projects.Select(p => p.Name).ToHashSet();
        var referenced = Seed.ServiceEndpoints.SelectMany(e => e.Projects ?? []).Concat(Seed.VariableGroups!.SelectMany(g => g.Projects ?? []))
            .Concat(Seed.Environments!.Select(e => e.Project)).Concat(Seed.Policies!.Select(p => p.Project)).Concat(Seed.PullRequests!.Select(p => p.Project))
            .Concat(Seed.WorkItems!.Select(w => w.Project)).Concat(Seed.Hooks!.Select(h => h.Project).OfType<string>());
        Assert.All(referenced, p => Assert.Contains(p, projects));
        Assert.All(Seed.Environments!, e => Assert.Equal(e.Name.ToLowerInvariant(), e.Name));
    }

    [Fact]
    public void Pull_requests_runs_policies_and_hooks_point_at_things_that_exist()
    {
        foreach (var pr in Seed.PullRequests!)
        {
            var repo = RepoOf(pr.Project, pr.Repo);
            Assert.True(BranchExists(repo, Short(pr.Source)), $"PR {pr.Id} source {pr.Source}");
            Assert.True(BranchExists(repo, Short(pr.Target)), $"PR {pr.Id} target {pr.Target}");
            Assert.NotEqual(Short(pr.Source), Short(pr.Target));
            if (pr.Status != "active") Assert.NotNull(pr.Closed);
            foreach (var id in pr.WorkItems ?? [])
            {
                var item = Seed.WorkItems!.Single(w => w.Id == id);
                Assert.Equal(pr.Project, item.Project);
                Assert.Contains(item.Links!, l => l.Kind == "pullrequest" && l.To == pr.Id && l.Repo == pr.Repo);
            }
        }
        foreach (var run in Seed.Runs!)
        {
            var (project, pipeline) = Seed.Projects.SelectMany(p => (p.Pipelines ?? []).Select(x => (p, x))).Single(x => x.x.Id == run.Pipeline);
            Assert.True(BranchExists(project.Repos!.Single(r => r.Name == pipeline.Repo), run.Branch), $"run {run.Id} branch {run.Branch}");
            Assert.Contains(run.Result, new[] { "succeeded", "failed", "canceled" });
        }
        foreach (var policy in Seed.Policies!)
        {
            if (policy.Pipeline is { } id) Assert.Contains(Seed.Projects.Single(p => p.Name == policy.Project).Pipelines!, p => p.Id == id);
            if (policy.Repo is not null) Assert.True(BranchExists(RepoOf(policy.Project, policy.Repo), Short(policy.Branch!)));
        }
        foreach (var hook in Seed.Hooks!.Where(h => h.Repo is not null)) Assert.NotNull(RepoOf(hook.Project!, hook.Repo!));
        foreach (var group in Seed.VariableGroups!.Where(g => g.KeyVault is not null))
            Assert.Equal("azurerm", Seed.ServiceEndpoints!.Single(e => e.Name == group.KeyVaultEndpoint).Type);
    }

    [Fact]
    public void Work_items_form_a_consistent_hierarchy()
    {
        var items = Seed.WorkItems!.ToDictionary(w => w.Id);
        foreach (var item in items.Values)
            foreach (var link in item.Links ?? [])
            {
                if (link.Kind is "child" or "parent" or "related")
                {
                    Assert.True(items.ContainsKey(link.To!.Value), $"{item.Id} -> {link.To}");
                    var back = link.Kind switch { "child" => "parent", "parent" => "child", _ => "related" };
                    Assert.Contains(items[link.To.Value].Links!, l => l.Kind == back && l.To == item.Id);
                }
                else if (link.Kind == "commit")
                {
                    var repo = RepoOf(item.Project, link.Repo!);
                    Assert.False(repo.Empty);
                    if (link.Ref is { } reference && reference.StartsWith("refs/heads/")) Assert.True(BranchExists(repo, Short(reference)));
                    if (link.Ref is { } tag && tag.StartsWith("refs/tags/"))
                        Assert.Contains(tag["refs/tags/".Length..], (repo.Tags ?? []).Concat(repo.AnnotatedTags ?? []));
                }
                else Assert.Equal("pullrequest", link.Kind);
            }
        Assert.Superset(new HashSet<string> { "Epic", "Feature", "User Story", "Bug", "Task" }, items.Values.Select(w => w.Type).ToHashSet());
        Assert.Contains(items.Values, w => w.Links?.Any(l => l.Kind == "commit") == true);
        Assert.Contains(items.Values, w => w.Links?.Any(l => l.Kind == "pullrequest") == true);
    }

    [Fact]
    public void The_seed_text_never_contains_credentials()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Seed, SeedSpec.JsonOptions);
        Assert.DoesNotMatch(@"(?i)""(password|secret|token|apikey|privatekey|accessToken|serviceprincipalkey)""\s*:\s*""[^""]", json);
        Assert.DoesNotMatch(@"(?i)(ghp_|github_pat_|xox[bp]-|AKIA[0-9A-Z]{12}|-----BEGIN)", json);
        Assert.All(Seed.VariableGroups!.SelectMany(g => g.Variables).Where(v => v.Secret), v => Assert.Null(v.Value));
        Assert.All(Seed.Projects.SelectMany(p => p.Pipelines!).SelectMany(p => p.Variables ?? []).Where(v => v.Secret), v => Assert.Null(v.Value));
    }
}
