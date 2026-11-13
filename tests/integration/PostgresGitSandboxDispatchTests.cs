using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGitSandboxDispatchTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(opt =>
                opt.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTransferAttemptSchema.InitializeAsync(services);
        await GitSandboxDispatchSchema.InitializeAsync(services);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_replicas_enqueue_once_and_only_one_can_send()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var attemptId = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
            await InsertReviewedBlockedAttemptAsync(setup, attemptId, request);
        var options = Options(request.SandboxId);
        async Task<GitSandboxDispatchJob> SubmitAsync()
        {
            await using var db = fixture.NewContext();
            return await Store(db, options).EnqueueAsync(attemptId, request, default);
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => SubmitAsync()));
        Assert.Single(results.Select(x => x.Id).Distinct());

        async Task<GitSandboxDispatchClaim?> AcquireAsync()
        {
            await using var db = fixture.NewContext();
            return await Store(db, options).ClaimNextAsync(default);
        }
        var claims = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => AcquireAsync()));
        var claimed = Assert.Single(claims.Where(x => x is not null))!;
        Assert.Equal(GitSandboxDispatchStates.MayHaveTransmitted,
            claimed.Job.State);
        Assert.Null(await AcquireAsync());

        await using var verify = fixture.NewContext();
        var store = Store(verify, options);
        Assert.True(await store.RequireInspectionAsync(claimed,
            "SandboxOutcomeRequiresInspection", default));
        Assert.False(await store.RequireInspectionAsync(claimed,
            "SandboxOutcomeRequiresInspection", default));
        var review = await store.ClaimInspectionAsync(default);
        Assert.NotNull(review);
        Assert.True(await store.CompleteAsync(review!,
            GitSandboxDispatchStates.NeedsOperator, new string('a', 64),
            "LocalOutcomeDiverged", default));
        Assert.Equal(new[] {
            "Queued", "PotentialSend", "InspectionRequired",
            "InspectionStarted", "NeedsOperator"
        }, (await store.EventsAsync(attemptId, default)).Select(x => x.Type));
        Assert.False((await store.GetAsync(attemptId, default))!.RemoteWritesPerformed);
    }

    [Fact]
    public async Task Crashed_worker_claim_is_only_inspectable_after_deadline()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var attempt = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
            await InsertReviewedBlockedAttemptAsync(setup, attempt, request);
        var clock = new AdjustableClock(DateTimeOffset.UtcNow);
        var options = Options(request.SandboxId);
        await using var db = fixture.NewContext();
        var store = Store(db, options, clock);
        var queued = await store.EnqueueAsync(attempt, request, default);
        var sent = await store.ClaimNextAsync(default);
        Assert.NotNull(sent);
        Assert.Null(await store.ClaimNextAsync(default));
        Assert.Null(await store.ClaimInspectionAsync(default));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await store.ClaimNextAsync(default));
        var inspect = await store.ClaimInspectionAsync(default);
        Assert.NotNull(inspect);
        Assert.NotEqual(sent!.ClaimToken, inspect!.ClaimToken);
        Assert.False(await store.CompleteAsync(sent,
            GitSandboxDispatchStates.Verified, new string('a', 64),
            "LocalGitVerified", default));
        Assert.True(await store.CompleteAsync(inspect,
            GitSandboxDispatchStates.NeedsOperator, new string('0', 64),
            "LocalOutcomeDiverged", default));
        Assert.Equal(GitSandboxDispatchStates.NeedsOperator,
            (await store.GetAsync(attempt, default))!.State);
        Assert.Null(await store.ClaimNextAsync(default));
        Assert.Equal(1, (await store.EventsAsync(attempt, default))
            .Count(x => x.Type == "PotentialSend"));
    }

    [Fact]
    public async Task Real_git_object_transfer_from_database_queue_is_terminal_verified()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var attemptId = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
            await InsertReviewedBlockedAttemptAsync(setup, attemptId, request);
        var options = Options(request.SandboxId);
        await using var db = fixture.NewContext();
        var store = Store(db, options);
        var initial = await store.EnqueueAsync(attemptId, request, default);
        var runner = sandbox.Runner();
        var worker = new GitSandboxDispatchProcessor(store, runner);
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.False(await worker.ProcessOneAsync(default));
        Assert.False(await worker.InspectOneAsync(default));
        var complete = await store.GetAsync(attemptId, default);
        Assert.NotNull(complete);
        Assert.Equal(GitSandboxDispatchStates.Verified, complete.State);
        Assert.Equal("LocalGitVerified", complete.OutcomeCode);
        Assert.Equal(64, complete.OutcomeSha256!.Length);
        Assert.False(complete.RemoteWritesPerformed);
        Assert.False(complete.ExecutionAuthorized);
        Assert.False(complete.ReadyForExecution);
        Assert.Equal(request.SourceRefs, sandbox.TargetRefs());
        Assert.Equal(initial.Id,
            (await store.EnqueueAsync(attemptId, request, default)).Id);
        Assert.Equal(new[] { "Queued", "PotentialSend", "Verified" },
            (await store.EventsAsync(attemptId, default)).Select(x => x.Type));
    }

    [Fact]
    public async Task Lost_response_after_a_real_push_is_reconciled_without_retransmission()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var attempt = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
            await InsertReviewedBlockedAttemptAsync(setup, attempt, request);
        var options = Options(request.SandboxId);
        await using var db = fixture.NewContext();
        var store = Store(db, options);
        await store.EnqueueAsync(attempt, request, default);
        var lost = sandbox.Runner(_ =>
            throw new IOException("test lost acknowledgement"));
        Assert.True(await new GitSandboxDispatchProcessor(store, lost)
            .ProcessOneAsync(default));
        Assert.Equal(GitSandboxDispatchStates.InspectionRequired,
            (await store.GetAsync(attempt, default))!.State);
        Assert.Equal(request.SourceRefs, sandbox.TargetRefs());
        Assert.Null(await store.ClaimNextAsync(default));
        Assert.True(await new GitSandboxDispatchProcessor(store, sandbox.Runner())
            .InspectOneAsync(default));
        Assert.Equal(GitSandboxDispatchStates.ReconciledMatch,
            (await store.GetAsync(attempt, default))!.State);
        Assert.Equal(new[] {
            "Queued", "PotentialSend", "InspectionRequired",
            "InspectionStarted", "ReconciledMatch"
        }, (await store.EventsAsync(attempt, default)).Select(x => x.Type));
        Assert.Null(await store.ClaimNextAsync(default));
    }

    [Fact]
    public async Task Database_denies_remote_writes_and_unreviewed_attempts()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var attempt = Guid.NewGuid();
        var options = Options(request.SandboxId);
        await using var db = fixture.NewContext();
        var store = Store(db, options);
        var denied = await Assert.ThrowsAsync<GitSandboxDispatchException>(
            () => store.EnqueueAsync(attempt, request, default));
        Assert.Equal("SandboxDispatchAttemptNotReviewed", denied.Code);

        await InsertReviewedBlockedAttemptAsync(db, attempt, request);
        var queued = await store.EnqueueAsync(attempt, request, default);
        // An API replica which does not allowlist the target must not even
        // acquire it transiently before discovering its wrong UUID.
        var unauthorizedWorker = Store(db, Options(Guid.NewGuid()));
        Assert.Null(await unauthorizedWorker.ClaimNextAsync(default));
        Assert.Null(await unauthorizedWorker.ClaimInspectionAsync(default));
        Assert.Equal(GitSandboxDispatchStates.Queued,
            (await store.GetAsync(attempt, default))!.State);
        var disabled = Store(db, new(false, new HashSet<Guid> {
            request.SandboxId
        }));
        Assert.Equal("SandboxDispatchDisabled",
            (await Assert.ThrowsAsync<GitSandboxDispatchException>(() =>
                disabled.EnqueueAsync(attempt, request, default))).Code);
        Assert.Equal("SandboxNotAllowlisted",
            (await Assert.ThrowsAsync<GitSandboxDispatchException>(() =>
                Store(db, Options(Guid.NewGuid())).EnqueueAsync(
                    attempt, request, default))).Code);

        Func<Task>[] unsafeUpdates = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_sandbox_dispatches SET remote_writes_performed = {true} WHERE id = {queued.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_sandbox_dispatches SET execution_authorized = {true} WHERE id = {queued.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_sandbox_dispatches SET ready_for_execution = {true} WHERE id = {queued.Id}")
        ];
        foreach (var forbidden in unsafeUpdates)
            await Assert.ThrowsAnyAsync<Exception>(forbidden);
        Assert.False((await store.GetAsync(attempt, default))!.RemoteWritesPerformed);
        Assert.False((await store.GetAsync(attempt, default))!.ExecutionAuthorized);
    }

    [Fact]
    public async Task Separate_attempt_cannot_enqueue_onto_an_active_local_target()
    {
        using var sandbox = new Sandbox();
        var request = await sandbox.BuildAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var db = fixture.NewContext();
        await InsertReviewedBlockedAttemptAsync(db, first, request);
        var distinctRequest = request with { Packet = request.Packet with {
            ScopeId = Guid.NewGuid()
        }};
        await InsertReviewedBlockedAttemptAsync(db, second, distinctRequest);
        var store = Store(db, Options(request.SandboxId));
        await store.EnqueueAsync(first, request, default);
        var error = await Assert.ThrowsAsync<GitSandboxDispatchException>(
            () => store.EnqueueAsync(second, distinctRequest, default));
        Assert.Equal("SandboxDispatchActiveTargetBusy", error.Code);
        Assert.Equal(GitSandboxDispatchStates.Queued,
            (await store.GetAsync(first, default))!.State);
        Assert.Null(await store.GetAsync(second, default));
    }

    private static PostgresGitSandboxDispatchStore Store(
        AppDbContext db, GitSandboxDispatchOptions options,
        TimeProvider? clock = null) => new(db, clock ?? TimeProvider.System, options);

    private static GitSandboxDispatchOptions Options(Guid sandboxId) =>
        new(true, new HashSet<Guid> { sandboxId });

    private static async Task InsertReviewedBlockedAttemptAsync(
        AppDbContext db, Guid attemptId, LocalGitCreateOnlyTransferRequest request)
    {
        var packet = request.Packet;
        var requester = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution
            ) VALUES (
                {attemptId}, {packet.PreparationId}, {packet.PlanId},
                {packet.ScopeId}, {packet.RepositoryId},
                {packet.Organization}, {packet.Repository},
                {packet.EvidenceSha256}, {new string('d', 64)},
                {packet.CandidateSetSha256}, {packet.CreateCandidates}, {now},
                {Guid.NewGuid()}, {1L}, {requester}, {reviewer},
                {now}, {now}, {now}, NULL,
                'BlockedAtWriteBarrier', false, false, false, false)
            """);
    }

    private sealed class AdjustableClock(DateTimeOffset time) : TimeProvider
    {
        private DateTimeOffset _now = time;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f049-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "sources");
        public string BundleRoot => Path.Combine(Root, "bundles");
        public string TargetRoot => Path.Combine(Root, "targets");
        public string ScratchRoot => Path.Combine(Root, "scratch");
        public Guid SandboxId { get; } = Guid.NewGuid();
        public Guid WorkpackId { get; } = Guid.NewGuid();
        public Guid PlanId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        private string Target => Path.Combine(TargetRoot,
            SandboxId.ToString("N") + ".git");

        public Sandbox()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(TargetRoot);
            Directory.CreateDirectory(ScratchRoot);
        }

        public LocalGitCreateOnlyTransferRunner Runner(
            Func<CancellationToken, Task>? hook = null) => new(
                BundleRoot, TargetRoot, ScratchRoot, enabled: true,
                testAfterPush: hook);

        public async Task<LocalGitCreateOnlyTransferRequest> BuildAsync()
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", work);
            Git("-C", work, "config", "user.email", "f049@example.invalid");
            Git("-C", work, "config", "user.name", "Sandbox Git Test");
            File.WriteAllText(Path.Combine(work, "hello.txt"), "F-049\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "initial");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "tag", "-a", "v1", "-m", "annotated");
            Git("-C", work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(work, "next.txt"), "branch\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "feature");
            Git("-C", work, "checkout", "-q", "main");

            var source = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
                SourceRoot, ConnectionId, "f049-test-repo");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Git("clone", "--bare", "--quiet", work, source);
            var refs = ReadRefs(source);
            var acquirer = new LocalGitSourceBundleAcquirer(
                SourceRoot, BundleRoot);
            var bundle = await acquirer.AcquireAsync(new(
                WorkpackId, ConnectionId, "f049-test-repo",
                new string('a', 64), new string('b', 64), refs), default);
            var view = new GitSourceBundleView(
                WorkpackId, PlanId, Guid.NewGuid(), "f049-test-repo",
                bundle.SourceEvidenceSha256, new string('a', 64),
                bundle.BundleFileName, bundle.BundleSha256, bundle.BundleBytes,
                bundle.RefCount, bundle.AcquiredAtUtc, true, false);

            Git("init", "--bare", "--quiet", Target);
            var packet = new GitHubCreateOnlyPacket(
                Guid.NewGuid(), PlanId, Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkpackId,
                Random.Shared.NextInt64(10_000_000_000, 200_000_000_000),
                "gh-org", "sandbox", new string('c', 64),
                bundle.BundleSha256, new string('a', 64),
                new string('d', 64), new string('e', 64),
                new string('f', 64), new string('1', 64),
                new string('2', 64), new string('3', 64),
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", refs),
                new string('4', 64), refs.Count, 0, 0,
                GitHubCreateOnlyPacketIntegrity.State,
                false, false, false, false);
            return new(packet, view, SandboxId, refs, [], refs);
        }

        public IReadOnlyList<GitMirrorRef> TargetRefs() => ReadRefs(Target);

        private static IReadOnlyList<GitMirrorRef> ReadRefs(string bare)
        {
            var lines = Git("--git-dir=" + bare, "for-each-ref",
                "--format=%(refname)%09%(objectname)");
            return GitMirrorRefReconciler.CanonicalizeObservedRefs(
                lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.TrimEnd('\r').Split('\t'))
                    .Select(x => new GitMirrorRef(x[0], x[1])).ToArray());
        }

        private static string Git(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) throw new InvalidOperationException("Git unavailable");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000) || process.ExitCode != 0)
                throw new InvalidOperationException("Local fixture failed " + stderr);
            return stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
