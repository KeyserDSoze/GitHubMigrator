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
        // The destination-only new commit descends from the source tip,
        // even though that commit does not exist in the source mirror.
        Assert.Equal(GitIncrementalAncestryStatus.TargetAheadPreserved,
            kinds["refs/heads/unknown"]);
        // Neither local mirror has both tips: do not guess ancestry.
        Assert.Equal(GitIncrementalAncestryStatus.HistoryUnavailableBlocked,
            kinds["refs/heads/unavailable"]);
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

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F096_reads_only_the_workpack_physical_mirror_not_logical_snapshot(
        string format)
    {
        using var repos = new Fixture();
        var legacy = repos.Setup(format);
        var physicalId = Guid.NewGuid();
        var physicalPath = Path.Combine(repos.SourceRoot,
            physicalId.ToString("N") + ".git");
        Directory.Move(repos.Source, physicalPath);
        var bound = legacy with {
            SourcePhysicalMirrorId = physicalId,
            SourcePhysicalMirrorProofSha256 = new string('d', 64)
        };
        var audited = await repos.Auditor().AuditAsync(bound, default);
        GitIncrementalAncestryIntegrity.VerifyReceipt(audited);
        Assert.Equal(physicalId, audited.Request.SourcePhysicalMirrorId);
        Assert.Equal(format, audited.GitObjectFormat);
        Assert.Contains(audited.Items, x =>
            x.Status == GitIncrementalAncestryStatus.FastForwardCandidateBlocked);
        Assert.False(audited.RemoteTransportInvoked);
        Assert.False(audited.CanPush);
        Assert.NotEqual(legacy.SourceSnapshotId, physicalId);

        var missing = await Assert.ThrowsAsync<GitIncrementalAncestryException>(
            () => repos.Auditor().AuditAsync(bound with {
                SourcePhysicalMirrorId = Guid.NewGuid()
            }, default));
        Assert.Equal("GitAncestryLocalEvidenceRejected", missing.Code);
        // F-094 v1 cannot reuse this F-060 directory by logical snapshot ID.
        var legacyError = await Assert.ThrowsAsync<GitIncrementalAncestryException>(
            () => repos.Auditor().AuditAsync(legacy, default));
        Assert.Equal("GitAncestryLocalEvidenceRejected", legacyError.Code);
    }

    // F-097c.1: a real F-094 local SHA-1/SHA-256 audit must bind the
    // complete F-097 chain to the physical GUID mirror and object closure.
    // This intentionally does not claim a persisted F-095 later-epoch row.
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F097c_v3_later_epoch_full_refs_bind_to_real_F060_mirror(
        string format)
    {
        using var repos = new Fixture();
        var legacy = repos.Setup(format);
        var sourceId = Guid.NewGuid();
        var preparationId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var manifestSha = new string('c', 64);
        var now = DateTimeOffset.UtcNow;

        var plan = new IncrementalOperationPlan(
            planId, preparationId, baselineId, sourceId,
            Guid.NewGuid(), "gh-org", new string('a', 64),
            new string('b', 64), new string('d', 64),
            manifestSha, now, Guid.NewGuid(),
            1, 6, 1, 0, true, false, [], [
                new IncrementalOperationStep(1, null, "repo-1",
                    "target", "ReviewTargetName", "Blocked", "Blocked", 0)
            ]);

        GitHubSourceSyncCheckpointReceipt Checkpoint(
            Guid snapshot, GitHubSourceSyncCheckpointReceipt? previous)
        {
            var count = previous is null ? 0 : 1;
            var epoch = new GitHubSourceSyncCheckpointProof(
                preparationId, planId, sourceId, snapshot, baselineId,
                previous?.Id, previous?.EvidenceSha256, manifestSha,
                now.AddMinutes(-1), now, false, previous is null,
                0, count, 0, 0, count, 0,
                [new GitHubSourceRefDelta("repo-1", "target",
                    0, count, 0, 0)]);
            var digest = GitHubSourceSyncCheckpointIntegrity.Digest(epoch);
            var refManifest = new GitHubSourceRefChangeManifest(
                sourceId, snapshot, 0, count, 0, 0, new string('e', 64));
            var id = Guid.NewGuid();
            return new GitHubSourceSyncCheckpointReceipt(
                id, epoch, digest, Guid.NewGuid(), now,
                false, false, false, false, refManifest,
                GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                    id, digest, refManifest), 2);
        }

        var baseline = Checkpoint(baselineId, null);
        var verifiedBaseline = VerifiedSourceGitRefIntegrity.Seal(
            baselineId, sourceId, "repo-1", new string('b', 64),
            legacy.ExpectedSourceRefs);
        var prior = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            baseline, verifiedBaseline);
        var checkpoint = Checkpoint(legacy.SourceSnapshotId, baseline);
        var source = VerifiedSourceGitRefIntegrity.Seal(
            checkpoint.Proof.SnapshotId, sourceId, "repo-1",
            new string('b', 64), legacy.ExpectedSourceRefs);
        var fullRef = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            checkpoint, source, prior);
        Assert.Equal(prior.EvidenceSha256, fullRef.PreviousFullRefProofSha256);

        // Relocate the real bare source so F-094 can only read the F-060
        // physical mirror ID, not the F-090 logical snapshot path.
        var workpack = Guid.NewGuid();
        var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            checkpoint.Proof.SnapshotId, workpack);
        var mirrorPath = Path.Combine(repos.SourceRoot,
            mirrorId.ToString("N") + ".git");
        Directory.Move(repos.Source, mirrorPath);
        var sample = legacy with {
            SourceCheckpointId = checkpoint.Id,
            SourceCheckpointAttestationSha256 =
                checkpoint.RefChangeAttestationSha256!,
            SourcePhysicalMirrorId = mirrorId,
            SourcePhysicalMirrorProofSha256 = new string('d', 64)
        };
        var physical = await repos.Auditor().AuditAsync(sample, default);
        Assert.Equal(format, physical.GitObjectFormat);

        var refSetSha = GitSourceSnapshotMirrorIntegrity.SourceRefsSha(
            source.Refs);
        var bundleSha = new string('a', 64);
        var mirrorProofSha = GitSourceSnapshotMirrorIntegrity.MirrorEvidenceSha(
            source.SnapshotId, mirrorId, workpack, bundleSha,
            source.SourceEvidenceSha256, refSetSha, source.Refs.Count,
            physical.SourceObjectSetSha256,
            physical.SourceReachableObjectCount);
        var mirror = new GitIncrementalSourceMirrorBinding(
            mirrorId, source.SnapshotId, workpack, plan.Id, Guid.NewGuid(),
            "repo-1", source.SourceEvidenceSha256, bundleSha,
            source.Refs.Count, refSetSha, physical.SourceObjectSetSha256,
            physical.SourceReachableObjectCount, mirrorProofSha,
            physical.VerifiedAtUtc.AddSeconds(-1),
            GitSourceMirrorLifecycleStates.Verified,
            false, false, false, false);

        var v3 = sample with {
            SourcePhysicalMirrorProofSha256 = mirrorProofSha,
            SourceCheckpointGitRefProofSha256 = fullRef.EvidenceSha256
        };
        var audited = await repos.Auditor().AuditAsync(v3, default);
        var time = audited.VerifiedAtUtc.AddSeconds(1);
        GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
            plan, checkpoint, source, fullRef, prior,
            mirror, audited, time, requireFresh: true);
        Assert.Equal(format, audited.GitObjectFormat);
        Assert.False(audited.RemoteTransportInvoked);
        Assert.False(audited.CanPush);
        Assert.False(audited.CanRetry);
        Assert.False(audited.MigrationComplete);
        Assert.NotEqual(physical.EvidenceSha256, audited.EvidenceSha256);
        Assert.Equal(audited.EvidenceSha256,
            (await repos.Auditor().AuditAsync(v3, default)).EvidenceSha256);

        // Mutations of any one of the trusted checkpoints, chain,
        // physical mirror root/identity or receipt digest must fail closed.
        foreach (var altered in new[] {
            audited with { Request = v3 with {
                SourceCheckpointGitRefProofSha256 = new string('f', 64) } },
            audited with { Request = v3 with {
                SourcePhysicalMirrorProofSha256 = new string('f', 64) } },
            audited with { Request = v3 with {
                SourceCheckpointId = Guid.NewGuid() } },
            audited with { SourceObjectSetSha256 = new string('f', 64) }
        })
            Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
                GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
                    plan, checkpoint, source, fullRef, prior,
                    mirror, altered, time, true));

        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
                plan, checkpoint, source, fullRef with {
                    EvidenceSha256 = new string('f', 64) }, prior,
                mirror, audited, time, true));
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
                plan, checkpoint, source, fullRef, null,
                mirror, audited, time, true));
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
                plan, checkpoint, source, fullRef, prior,
                mirror with { WorkpackId = Guid.NewGuid() },
                audited, time, true));
        Assert.Throws<GitIncrementalAncestryLedgerException>(() =>
            GitSourceCheckpointPhysicalAncestryIntegrity.Verify(
                plan, checkpoint, source, fullRef, prior,
                mirror, audited, time.AddMinutes(3), true));
        Assert.Equal("GitAncestryEvidenceInvalid",
            (await Assert.ThrowsAsync<GitIncrementalAncestryException>(() =>
                repos.Auditor().AuditAsync(v3 with {
                    SourcePhysicalMirrorId = null,
                    SourcePhysicalMirrorProofSha256 = null
                }, default))).Code);
        // Same source object set under a different declared F-097 SHA
        // produces a DIFFERENT versioned receipt hash, not an alias.
        var different = await repos.Auditor().AuditAsync(
            v3 with { SourceCheckpointGitRefProofSha256 = new string('f', 64) },
            default);
        Assert.NotEqual(audited.EvidenceSha256, different.EvidenceSha256);
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
            var parentTree = Git("--git-dir=" + Target,
                "rev-parse", second + "^{tree}").Trim();
            var newTarget = Git("--git-dir=" + Target,
                "-c", "user.name=F094 Fixture",
                "-c", "user.email=f094@example.invalid",
                "commit-tree", parentTree,
                "-p", second, "-m", "new target-only commit").Trim();
            // Created after the clones: each new commit is absent from
            // the opposing mirror, although both descend from second.
            var newSource = Git("--git-dir=" + Source,
                "-c", "user.name=F094 Fixture",
                "-c", "user.email=f094@example.invalid",
                "commit-tree", parentTree,
                "-p", second, "-m", "new source-only commit").Trim();

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
            Put(Source, "refs/heads/unavailable", newSource);
            Put(Target, "refs/heads/unavailable", newTarget);
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
