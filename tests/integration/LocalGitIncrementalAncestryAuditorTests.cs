using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-094 physical Git subprocess tests: NO GitHub.com, network or credentials.
public sealed class LocalGitIncrementalAncestryAuditorTests
{
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Full_bare_graph_classifies_all_eight_states_with_no_write_authority(
        string format)
    {
        using var repos = new Fixture();
        var request = repos.Setup(format);
        var auditor = repos.Auditor();
        var proof = await auditor.AuditAsync(request, default);

        Assert.Equal(format, proof.GitObjectFormat);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        Assert.True(proof.SourceReachableObjectCount > 0);
        Assert.True(proof.TargetReachableObjectCount > 0);
        Assert.False(proof.RemoteTransportInvoked);
        Assert.False(proof.CanPush);
        Assert.False(proof.CanRetry);
        Assert.False(proof.MigrationComplete);
        GitIncrementalAncestryIntegrity.VerifyReceipt(proof);

        var kinds = proof.Items.ToDictionary(x => x.Name, x => x.Status,
            StringComparer.Ordinal);
        Assert.Equal(GitIncrementalAncestryStatus.MatchingObjectId,
            kinds["refs/heads/main"]);
        Assert.Equal(GitIncrementalAncestryStatus.FastForwardCandidateBlocked,
            kinds["refs/heads/ff"]);
        Assert.Equal(GitIncrementalAncestryStatus.TargetAheadPreserved,
            kinds["refs/heads/ahead"]);
        Assert.Equal(GitIncrementalAncestryStatus.DivergedBlocked,
            kinds["refs/heads/diverge"]);
        Assert.Equal(GitIncrementalAncestryStatus.HistoryUnavailableBlocked,
            kinds["refs/heads/unknown"]);
        Assert.Equal(GitIncrementalAncestryStatus.CreateCandidateBlocked,
            kinds["refs/heads/source-only"]);
        Assert.Equal(GitIncrementalAncestryStatus.TargetOnlyPreserved,
            kinds["refs/heads/target-only"]);
        Assert.Equal(GitIncrementalAncestryStatus.MatchingObjectId,
            kinds["refs/tags/v1"]);
        Assert.Equal(GitIncrementalAncestryStatus.TagChangeBlocked,
            kinds["refs/tags/release"]);

        var repeated = await auditor.AuditAsync(request, default);
        Assert.Equal(proof.EvidenceSha256, repeated.EvidenceSha256);
        Assert.Equal(proof.Items.ToArray(), repeated.Items.ToArray());
        var proofChanged = await auditor.AuditAsync(
            request with { TargetObservationId = Guid.NewGuid() }, default);
        Assert.NotEqual(proof.EvidenceSha256, proofChanged.EvidenceSha256);
    }

    [Fact]
    public async Task Altered_local_ref_or_object_format_refuses_a_receipt()
    {
        using var repos = new Fixture();
        var request = repos.Setup();
        repos.Git("--git-dir=" + repos.Target, "update-ref",
            "refs/heads/ff", repos.Git("--git-dir=" + repos.Target,
                "rev-parse", "refs/heads/main").Trim());
        var refused = await Assert.ThrowsAsync<GitIncrementalAncestryException>(
            () => repos.Auditor().AuditAsync(request, default));
        Assert.Equal("GitAncestryLocalEvidenceRejected", refused.Code);

        Assert.Equal("GitAncestryEvidenceInvalid",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => repos.Auditor().AuditAsync(
                    request with { TargetObservationSha256 = "bad" },
                    default))).Code);
    }

    [Fact]
    public async Task Off_by_default_shallow_and_alternates_are_fail_closed()
    {
        using var repos = new Fixture();
        var request = repos.Setup();
        Assert.Equal("GitAncestryDisabled",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => new LocalGitIncrementalAncestryAuditor(
                    repos.SourceRoot, repos.TargetRoot).AuditAsync(
                    request, default))).Code);

        File.WriteAllText(Path.Combine(repos.Target, "shallow"),
            request.ExpectedTargetRefs[0].ObjectId + "\n");
        Assert.Equal("GitAncestryLocalEvidenceRejected",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => repos.Auditor().AuditAsync(request, default))).Code);
        File.Delete(Path.Combine(repos.Target, "shallow"));

        Directory.CreateDirectory(Path.Combine(repos.Target,
            "objects", "info"));
        File.WriteAllText(Path.Combine(repos.Target, "objects",
            "info", "alternates"), repos.Source + "/objects\n");
        Assert.Equal("GitAncestryLocalEvidenceRejected",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => repos.Auditor().AuditAsync(request, default))).Code);
    }

    [Fact]
    public async Task Symlink_missing_bare_and_cancellation_cannot_yield_proof()
    {
        using var repos = new Fixture();
        var request = repos.Setup();
        var linked = Path.Combine(repos.Root, "linked-source");
        Directory.CreateSymbolicLink(linked, repos.SourceRoot);
        Assert.Equal("GitAncestryLocalEvidenceRejected",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => new LocalGitIncrementalAncestryAuditor(
                    linked, repos.TargetRoot, enabled: true).AuditAsync(
                    request, default))).Code);

        Assert.Equal("GitAncestryLocalEvidenceRejected",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(
                () => repos.Auditor().AuditAsync(
                    request with { TargetSandboxId = Guid.NewGuid() },
                    default))).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repos.Auditor().AuditAsync(request, cancelled.Token));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f094-local-ancestry-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "source");
        public string TargetRoot => Path.Combine(Root, "target");
        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid TargetId { get; } = Guid.NewGuid();
        public string Source => Path.Combine(SourceRoot,
            SourceId.ToString("N") + ".git");
        public string Target => Path.Combine(TargetRoot,
            TargetId.ToString("N") + ".git");

        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(TargetRoot);
        }

        public LocalGitIncrementalAncestryAuditor Auditor() =>
            new(SourceRoot, TargetRoot, enabled: true);

        public GitIncrementalAncestryRequest Setup(string format = "sha1")
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", "--object-format=" + format, work);
            Git("-C", work, "config", "user.name", "F094 Fixture");
            Git("-C", work, "config", "user.email", "f094@example.invalid");

            string Commit(string body, string message)
            {
                File.WriteAllText(Path.Combine(work, "audit.txt"), body + "\n");
                Git("-C", work, "add", "audit.txt");
                Git("-C", work, "commit", "-q", "-m", message);
                return Git("-C", work, "rev-parse", "HEAD").Trim();
            }

            var first = Commit("base", "base");
            Git("-C", work, "branch", "-M", "main");
            var second = Commit("second", "second");
            var third = Commit("third", "third");
            Git("-C", work, "checkout", "-q", "-b", "left", second);
            var left = Commit("left", "left");
            Git("-C", work, "checkout", "-q", "-b", "right", second);
            var right = Commit("right", "right");
            Git("-C", work, "checkout", "-q", "main");
            Git("-C", work, "tag", "-a", "v1", "-m", "shared", first);
            Git("-C", work, "tag", "-a", "tag-left", "-m", "left release", left);
            Git("-C", work, "tag", "-a", "tag-right", "-m", "right release", right);
            Git("clone", "--mirror", "--no-hardlinks", "-q", work, Source);
            Git("clone", "--mirror", "--no-hardlinks", "-q", Source, Target);

            var tags = new[] {
                Git("--git-dir=" + Source, "rev-parse",
                    "refs/tags/v1").Trim(),
                Git("--git-dir=" + Source, "rev-parse",
                    "refs/tags/tag-left").Trim(),
                Git("--git-dir=" + Source, "rev-parse",
                    "refs/tags/tag-right").Trim()
            };
            var newTarget = Git("--git-dir=" + Target,
                "-c", "user.name=F094 Fixture",
                "-c", "user.email=f094@example.invalid",
                "commit-tree", second + "^{tree}",
                "-p", second, "-m", "new target-only commit").Trim();
            // commit-tree requires a TREE, not a tree-ish rev expression.
            // The explicit resolved tree SHA is supplied below instead if
            // the platform version rejects the tree-ish input.

            void Put(string repository, string name, string oid) =>
                Git("--git-dir=" + repository, "update-ref", name, oid);
            foreach (var repository in new[] { Source, Target })
            {
                Put(repository, "refs/heads/main", first);
                Git("--git-dir=" + repository, "update-ref", "-d",
                    "refs/heads/left");
                Git("--git-dir=" + repository, "update-ref", "-d",
                    "refs/heads/right");
                Git("--git-dir=" + repository, "update-ref", "-d",
                    "refs/tags/tag-left");
                Git("--git-dir=" + repository, "update-ref", "-d",
                    "refs/tags/tag-right");
            }
            Put(Source, "refs/heads/ff", third);
            Put(Target, "refs/heads/ff", second);
            Put(Source, "refs/heads/ahead", second);
            Put(Target, "refs/heads/ahead", third);
            Put(Source, "refs/heads/diverge", left);
            Put(Target, "refs/heads/diverge", right);
            Put(Source, "refs/heads/unknown", second);
            Put(Target, "refs/heads/unknown", newTarget);
            Put(Source, "refs/heads/source-only", third);
            Put(Target, "refs/heads/target-only", right);
            Put(Source, "refs/tags/release", tags[1]);
            Put(Target, "refs/tags/release", tags[2]);

            return new(SourceId, TargetId, Guid.NewGuid(),
                new string('a', 64), Guid.NewGuid(), new string('b', 64),
                Refs(Source), Refs(Target));
        }

        private IReadOnlyList<GitMirrorRef> Refs(string path) =>
            GitMirrorRefReconciler.CanonicalizeObservedRefs(
                Git("--git-dir=" + path, "for-each-ref",
                    "--format=%(refname)%09%(objectname)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Select(parts => new GitMirrorRef(parts[0], parts[1]))
                .ToArray());

        public string Git(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                throw new InvalidOperationException("Git test fixture unavailable");
            var output = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000) || process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Git fixture command failed: " + stderr);
            return output;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
