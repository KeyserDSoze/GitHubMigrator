using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;
using Xunit;
using Xunit.Abstractions;

namespace GitHubMigrator.LiveSmoke.Tests;

// Read-only checks of the real product GitHub readers against real github.com (default endpoints),
// including the behaviours the GitHub simulator was written from documentation for.
[Collection("live")]
public sealed class GitHubLiveSmokeTests(LiveReport report, ITestOutputHelper output)
{
    private static LiveGitHub Gh => LiveEnvironment.GitHub!;
    private static GitHubEndpoints Endpoints => LiveEnvironment.GhEndpoints;

    [LiveGitHubFact]
    public async Task The_product_validator_and_destination_access_reader_work_against_github_com()
    {
        var validator = new GitHubConnectionValidator(LiveEnvironment.ReadOnlyClient(), Endpoints);
        var ok = await validator.ValidateAsync(Gh.Organization, Gh.Pat, default);
        output.WriteLine($"validator: {ok.IsValid} code={ok.Code} login={ok.AuthenticatedAs}");
        Assert.True(ok.IsValid, "The token must authenticate and see the organization. Code: " + ok.Code);

        var access = await new GitHubDestinationAccessReader(LiveEnvironment.ReadOnlyClient(), Endpoints)
            .ReadAsync(Gh.Organization, ok.AuthenticatedAs!, Gh.Pat, default);
        output.WriteLine($"membership={access.MembershipStatus} role={access.MembershipRole} privateRepoPolicy={access.PrivateRepositoryPolicy}");
        report.Record("github", "destination-access", access.MembershipStatus == "Active" ? "confirmed" : "info",
            $"membership={access.MembershipStatus}; role={access.MembershipRole}; privateRepositoryPolicy={access.PrivateRepositoryPolicy}");

        var bogus = await validator.ValidateAsync(Gh.Organization, "ghp_invalidtokenforlivesmoketest0000000000", default);
        Assert.False(bogus.IsValid);
        report.Record("github", "validator-invalid-token-code", "info", "The product validator answers '" + bogus.Code + "' for an invalid token.");
    }

    [LiveGitHubFact]
    public async Task A_missing_repository_name_is_reported_as_not_visible_or_missing()
    {
        var probe = new GitHubTargetRepositoryProbe(LiveEnvironment.ReadOnlyClient(), Endpoints);
        var result = await probe.ProbeAsync(Gh.Organization, "gm-live-smoke-" + Guid.NewGuid().ToString("N")[..12], Gh.Pat, default);
        Assert.Equal("NotVisibleOrMissing", result);
        report.Record("github", "missing-repository-probe", "confirmed", "GET /repos/{org}/{missing} answers 404, reported as NotVisibleOrMissing (never as 'available').");
    }

    [LiveGitHubFact]
    public async Task Repository_level_readers_match_what_the_simulator_assumes()
    {
        if (Gh.Repository is null)
        {
            report.Record("github", "repository-readers", "info", "Set GITHUBMIGRATOR_LIVE_GH_REPO to also check commit lookups, rulesets and Actions configuration.");
            return;
        }

        // GitHub answers 422 "No commit found for SHA" for an absent commit; the product must call that "absent"
        var trace = new GitHubTraceabilityReader(LiveEnvironment.ReadOnlyClient(), Endpoints);
        var absent = await trace.CommitExistsAsync(Gh.Organization, Gh.Repository, new string('a', 40), Gh.Pat, default);
        Assert.False(absent);
        report.Record("github", "absent-commit-is-422-not-404", "confirmed", "An absent full SHA is reported as not found by the product reader (real GitHub: 422 'No commit found for SHA').");

        // The remaining probes depend on the token's scopes: a refusal is a finding, not a failure.
        await Probe("matching-refs", async () =>
        {
            var observation = await new GitHubRefObservationReader(LiveEnvironment.ReadOnlyClient(), TimeProvider.System, Endpoints)
                .ReadAsync(Gh.Organization, Gh.Repository, Gh.Pat, default);
            return ("confirmed", $"{observation.Refs.Count} refs read; {observation.Refs.Count(r => r.ObjectType == "tag")} annotated tag objects reported as type 'tag'.");
        });
        await Probe("rulesets-and-default-branch-protection", async () =>
        {
            var policy = await new GitHubImportPolicyReader(LiveEnvironment.ReadOnlyClient(), TimeProvider.System, Endpoints)
                .InspectAsync(Gh.Organization, Gh.Repository, Gh.Pat, default);
            return ("info", $"status={policy.RulesetStatus}; rulesets={policy.RulesetCount}; active={policy.ActiveRulesets}; defaultBranchProtection={policy.DefaultBranchProtection}");
        });
        await Probe("repository-metadata", async () =>
        {
            var repository = await new GitHubImportRepositoryReader(LiveEnvironment.ReadOnlyClient(), TimeProvider.System, Endpoints)
                .ObserveAsync(Gh.Organization, Gh.Repository, Gh.Pat, default);
            return ("info", $"status={repository.Status}; reportedPush={repository.ReportedPush}; archived={repository.Archived}; disabled={repository.Disabled}");
        });
        await Probe("actions-secret-and-variable-names", async () =>
        {
            var names = await new GitHubConfigurationReader(LiveEnvironment.ReadOnlyClient(), Endpoints)
                .GetNamesAsync(Gh.Organization, Gh.Repository, Gh.Pat, default);
            return ("info", $"secrets={names.Secrets.Count}; variables={names.Variables.Count} (names only; values are never readable)");
        });
    }

    // The publication plan reads one repository with GET requests only. These are the behaviours it was written from documentation for:
    // the branch tip, the contents API answering 404 for an absent path, git/ref answering 404 for an absent branch even when its name
    // contains "/", and the pull request list filtered by head=owner:branch.
    [LiveGitHubFact]
    public async Task The_publication_reader_matches_what_the_simulator_assumes()
    {
        if (Gh.Repository is null)
        {
            report.Record("github", "publication-reader", "info", "Set GITHUBMIGRATOR_LIVE_GH_REPO to check the read-only publication plan against the real repository.");
            return;
        }
        var reader = new GitHubPublicationReader(LiveEnvironment.ReadOnlyClient(), Endpoints);
        var unique = Guid.NewGuid().ToString("N")[..12];
        var target = await reader.ReadAsync(Gh.Organization, Gh.Repository, null, $"migrate/gm-live-smoke-{unique}", $".github/workflows/gm-live-smoke-{unique}.yml", new string('a', 64), null, Gh.Pat, default);
        output.WriteLine($"exists={target.RepositoryExists} archived={target.Archived} default={target.DefaultBranch} base={target.BaseBranch}@{target.BaseSha} file={target.WorkflowFile} head={target.HeadBranchExists} pulls={target.OpenPullRequests.Count}");
        Assert.True(target.RepositoryExists);
        Assert.False(string.IsNullOrEmpty(target.DefaultBranch));
        Assert.Equal(target.DefaultBranch, target.BaseBranch);
        Assert.Matches("^[0-9a-f]{40}$", target.BaseSha ?? "");
        Assert.Equal(WorkflowFileState.Absent, target.WorkflowFile);
        Assert.False(target.HeadBranchExists);
        Assert.Empty(target.OpenPullRequests);
        report.Record("github", "publication-reader", "confirmed",
            "Repository, default-branch tip, an absent workflow path (contents API 404), an absent branch whose name contains '/' (git/ref 404) and an empty head=owner:branch pull request list are read as the plan assumes.");

        var missing = await reader.ReadAsync(Gh.Organization, "gm-live-smoke-" + unique, null, "migrate/x", ".github/workflows/x.yml", new string('a', 64), null, Gh.Pat, default);
        Assert.False(missing.RepositoryExists);
        var noBase = await reader.ReadAsync(Gh.Organization, Gh.Repository, "gm-live-smoke-no-such-branch-" + unique, "migrate/x", ".github/workflows/x.yml", new string('a', 64), null, Gh.Pat, default);
        Assert.True(noBase.RepositoryExists);
        Assert.Null(noBase.BaseSha);
        report.Record("github", "publication-reader-missing", "confirmed", "A missing repository and a missing base branch are reported as such (404), not as an error.");
    }

    private async Task Probe(string name, Func<Task<(string Status, string Detail)>> probe)
    {
        try
        {
            var (status, detail) = await probe();
            report.Record("github", name, status, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or GitHubRefObservationException or InvalidOperationException)
        {
            // only the exception type and its stable code: never provider text or the token
            report.Record("github", name, "info", $"not available with this token: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
