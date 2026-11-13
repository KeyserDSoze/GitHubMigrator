using System.Diagnostics;
using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// No GitHub network, PAT or provider mock: execute the actual Linux Git CLI
// into a private temporary bare Git destination built from a real F-035 bundle.
public sealed class LocalGitImportRehearsalTests
{
    [Fact]
    public async Task Real_git_rehearsal_creates_only_missing_branches_and_tags()
    {
        using var fixture = new Fixture();
        var (request, expected) = await fixture.PrepareAsync();
        var proof = await fixture.Runner.RunAsync(request, default);

        Assert.Equal("sha1", proof.GitObjectFormat);
        Assert.Equal(2, proof.CreateCandidates);
        Assert.Equal(1, proof.AlreadyMatching);
        Assert.Equal(1, proof.TargetOnlyNotMaterialized);
        Assert.Equal(2, proof.DestinationCreated);
        Assert.Equal(GitImportRehearsalIntegrity.SealRefs(
            "f041-created-refs-v1", expected.Where(x =>
                x.Disposition == GitMirrorRefDisposition.CreateCandidate)
                .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray()),
            proof.CreatedRefsSha256);
        Assert.Equal(GitImportRehearsalIntegrity.SealRefs(
            "f041-source-refs-v1", expected.Where(x =>
                x.SourceObjectId is not null)
                .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray()),
            proof.VerifiedSourceRefsSha256);

        var sealedReceipt = GitImportRehearsalIntegrity.Seal(new(
            Guid.NewGuid(), request.Bundle.PlanId, request.WorkpackId,
            request.IntentId, request.IntentEvidenceSha256,
            request.ManifestSha256, request.ReconciliationSha256,
            request.BundleSha256, request.SafeRefPlanSha256,
            proof, Guid.NewGuid()));
        Assert.Equal(64, sealedReceipt.Length);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
        Assert.Single(Directory.GetFiles(fixture.BundleRoot, "*.bundle"));
    }

    [Fact]
    public async Task Corrupted_source_bundle_fails_before_any_local_git_import()
    {
        using var fixture = new Fixture();
        var (request, _) = await fixture.PrepareAsync();
        var path = Path.Combine(fixture.BundleRoot, request.Bundle.BundleFileName);
        await File.WriteAllBytesAsync(path,
            new byte[checked((int)request.Bundle.BundleBytes)]);
        var error = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            fixture.Runner.RunAsync(request, default));
        Assert.Equal("BundleStoredDigestMismatch", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Source_and_ref_plan_mismatch_never_create_a_destination()
    {
        using var fixture = new Fixture();
        var (request, _) = await fixture.PrepareAsync();
        var forgedPlan = request with {
            SafeRefPlanSha256 = new string('f', 64)
        };
        var error = await Assert.ThrowsAsync<GitImportRehearsalException>(() =>
            fixture.Runner.RunAsync(forgedPlan, default));
        Assert.Equal("GitRehearsalRefPlanNotSafe", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Cannot_make_an_execution_intent_from_divergent_ref()
    {
        using var fixture = new Fixture();
        var (request, expected) = await fixture.PrepareAsync();
        var altered = expected.Select(x =>
            x.Name == "refs/heads/feature"
                ? x with {
                    TargetObjectId = new string('f', 40),
                    Disposition = GitMirrorRefDisposition.DivergedBlocked
                }
                : x).ToArray();
        var unsafePlan = GitHubSafeRefPlanner.Build(
            request.Bundle.PlanId, request.ManifestSha256,
            request.ReconciliationSha256, request.BundleSha256, altered);
        var invalid = request with {
            SafeRefPlanSha256 = unsafePlan.EvidenceSha256, Refs = altered
        };
        Assert.False(unsafePlan.HasOnlyNonDestructiveCandidates);
        var error = await Assert.ThrowsAsync<GitImportRehearsalException>(() =>
            fixture.Runner.RunAsync(invalid, default));
        Assert.Equal("GitRehearsalRefPlanNotSafe", error.Code);
    }

    [Fact]
    public async Task Explicit_configuration_is_required_and_symlinked_scratch_is_rejected()
    {
        using var fixture = new Fixture();
        var (request, _) = await fixture.PrepareAsync();
        var disabled = new LocalGitImportRehearsalRunner(
            fixture.BundleRoot, fixture.ScratchRoot);
        Assert.Equal("GitRehearsalDisabled",
            (await Assert.ThrowsAsync<GitImportRehearsalException>(() =>
                disabled.RunAsync(request, default))).Code);
        var symlink = Path.Combine(fixture.Root, "scratch-link");
        Directory.CreateSymbolicLink(symlink, fixture.ScratchRoot);
        var unsafeRunner = new LocalGitImportRehearsalRunner(
            fixture.BundleRoot, symlink, enabled: true);
        Assert.Equal("GitRehearsalSymlinkBlocked",
            (await Assert.ThrowsAsync<GitImportRehearsalException>(() =>
                unsafeRunner.RunAsync(request, default))).Code);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f041-git-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "mirrors");
        public string BundleRoot => Path.Combine(Root, "bundles");
        public string ScratchRoot => Path.Combine(Root, "scratch");
        public Guid WorkpackId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public Guid PlanId { get; } = Guid.NewGuid();
        public LocalGitImportRehearsalRunner Runner { get; }

        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(ScratchRoot);
            Runner = new LocalGitImportRehearsalRunner(
                BundleRoot, ScratchRoot, enabled: true);
        }

        public async Task<(GitImportRehearsalRequest Request,
            IReadOnlyList<GitMirrorRefComparison> Refs)> PrepareAsync()
        {
            var working = Path.Combine(Root, "work");
            RunGit("init", "-q", working);
            RunGit("-C", working, "config", "user.email", "f041@example.invalid");
            RunGit("-C", working, "config", "user.name", "Git Transport Test");
            File.WriteAllText(Path.Combine(working, "main.txt"), "first\n");
            RunGit("-C", working, "add", ".");
            RunGit("-C", working, "commit", "-q", "-m", "main");
            RunGit("-C", working, "branch", "-M", "main");
            RunGit("-C", working, "tag", "-a", "v1", "-m", "signed-off release");
            RunGit("-C", working, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(working, "feature.txt"), "feature\n");
            RunGit("-C", working, "add", ".");
            RunGit("-C", working, "commit", "-q", "-m", "feature");
            RunGit("-C", working, "checkout", "-q", "main");

            var repoId = "some-repository";
            var source = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
                SourceRoot, ConnectionId, repoId);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            RunGit("clone", "--bare", "--quiet", working, source);
            var actualRefs = RunGit("--git-dir=" + source,
                "for-each-ref", "--format=%(refname)%09%(objectname)");
            var gitRefs = actualRefs.Trim().Split('\n')
                .Select(x => x.Split('\t'))
                .Select(x => new GitMirrorRef(x[0], x[1])).ToArray();
            Assert.Equal(3, gitRefs.Length);
            var acquirer = new LocalGitSourceBundleAcquirer(SourceRoot, BundleRoot);
            var bundle = await acquirer.AcquireAsync(new(
                WorkpackId, ConnectionId, repoId, new string('a',64),
                new string('b',64), gitRefs), default);
            var view = new GitSourceBundleView(
                WorkpackId, PlanId, Guid.NewGuid(), repoId,
                bundle.SourceEvidenceSha256, new string('a',64),
                bundle.BundleFileName, bundle.BundleSha256,
                bundle.BundleBytes, bundle.RefCount, bundle.AcquiredAtUtc,
                true, false);

            var refs = gitRefs.Select(x => new GitMirrorRefComparison(
                x.Name, x.ObjectId, x.Name == "refs/heads/main" ? x.ObjectId : null,
                x.Name == "refs/heads/main"
                    ? GitMirrorRefDisposition.MatchingObjectId
                    : GitMirrorRefDisposition.CreateCandidate, "None"))
                .Append(new GitMirrorRefComparison(
                    "refs/tags/zzz-target-only", null, new string('f', 40),
                    GitMirrorRefDisposition.TargetOnlyPreserved,
                    "TargetRefDeletionProhibited"))
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            var safe = GitHubSafeRefPlanner.Build(PlanId,
                new string('c',64), new string('a',64),
                bundle.BundleSha256, refs);
            Assert.Equal(2, safe.CreateCandidates);
            return (new(Guid.NewGuid(), new string('d',64),
                WorkpackId, new string('c',64), new string('a',64),
                bundle.BundleSha256, safe.EvidenceSha256, view, refs), refs);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }

        private static string RunGit(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) throw new InvalidOperationException("Git not installed");
            var text = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000) || process.ExitCode != 0)
                throw new InvalidOperationException("Git test fixture failed: " + error);
            return text;
        }
    }
}
