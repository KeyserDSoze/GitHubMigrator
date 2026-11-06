using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-049: a LOCAL-only durable dispatch. Nothing is an authorization to
// push to GitHub. The F-047 remote-write barrier remains permanently closed.
public static class GitSandboxDispatchStates
{
    public const string Queued = "Queued";
    public const string MayHaveTransmitted = "MayHaveTransmitted";
    public const string InspectionRequired = "InspectionRequired";
    public const string Inspecting = "Inspecting";
    public const string Verified = "Verified";
    public const string ReconciledMatch = "ReconciledMatch";
    public const string NeedsOperator = "NeedsOperator";
}

public sealed class GitSandboxDispatchException(string code)
    : Exception("Local Git sandbox dispatch refused")
{
    public string Code { get; } = code;
}

public sealed record GitSandboxDispatchOptions(
    bool Enabled, IReadOnlySet<Guid> AllowedSandboxIds);

public sealed record GitSandboxDispatchJob(
    Guid Id, Guid AttemptId, Guid SandboxId, long RepositoryId,
    string PacketSha256, string PayloadSha256, string State,
    Guid? ClaimToken, DateTimeOffset? ClaimUntilUtc,
    string? OutcomeSha256, string? OutcomeCode,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    bool RemoteWritesPerformed, bool ExecutionAuthorized,
    bool ReadyForExecution);

public sealed record GitSandboxDispatchEvent(
    long Id, Guid DispatchId, string Type, DateTimeOffset OccurredAtUtc);

public sealed record GitSandboxDispatchClaim(
    GitSandboxDispatchJob Job, LocalGitCreateOnlyTransferRequest Request,
    Guid ClaimToken);

public static class GitSandboxDispatchIntegrity
{
    public const int MaxSourceRefs = 512;
    public const int MaxPayloadBytes = 180_000;
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(10);

    public static string SealPayload(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "f049-local-dispatch-payload-v1\n" + json)));

    public static (string Payload, string Sha256) Prepare(
        LocalGitCreateOnlyTransferRequest request)
    {
        LocalGitCreateOnlyTransferIntegrity.Verify(request);
        if (request.SourceRefs.Count is < 1 or > MaxSourceRefs ||
            request.ExpectedTargetRefs.Count > MaxSourceRefs)
            throw new GitSandboxDispatchException("SandboxDispatchRefLimit");

        var json = JsonSerializer.Serialize(request);
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
            throw new GitSandboxDispatchException("SandboxDispatchPayloadLimit");
        return (json, SealPayload(json));
    }

    public static LocalGitCreateOnlyTransferRequest Open(
        string payload, string sha)
    {
        if (!GitSourceBundleStorageCheck.ValidSha(sha) ||
            Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes ||
            SealPayload(payload) != sha)
            throw new GitSandboxDispatchException("SandboxDispatchPayloadChanged");
        try
        {
            var request = JsonSerializer.Deserialize<LocalGitCreateOnlyTransferRequest>(
                payload) ?? throw new JsonException();
            LocalGitCreateOnlyTransferIntegrity.Verify(request);
            if (request.SourceRefs.Count > MaxSourceRefs ||
                request.ExpectedTargetRefs.Count > MaxSourceRefs)
                throw new GitSandboxDispatchException("SandboxDispatchRefLimit");
            return request;
        }
        catch (JsonException)
        {
            throw new GitSandboxDispatchException("SandboxDispatchPayloadChanged");
        }
    }

    public static bool ValidReceipt(GitSandboxDispatchClaim claim,
        LocalGitCreateOnlyTransferReceipt receipt) =>
        receipt.SandboxId == claim.Job.SandboxId &&
        receipt.GitHubRepositoryId == claim.Job.RepositoryId &&
        receipt.PacketSha256 == claim.Job.PacketSha256 &&
        receipt.State == LocalGitCreateOnlyTransferIntegrity.VerifiedState &&
        receipt.CreatedRefs == claim.Request.CreateRefs.Count &&
        receipt.LocalSandboxWritesPerformed &&
        !receipt.RemoteWritesPerformed && !receipt.ExecutionAuthorized &&
        !receipt.ReadyForExecution &&
        GitSourceBundleStorageCheck.ValidSha(receipt.EvidenceSha256);
}

public interface IGitSandboxDispatchStore
{
    Task<GitSandboxDispatchJob> EnqueueAsync(
        Guid attemptId, LocalGitCreateOnlyTransferRequest request,
        CancellationToken ct);
    Task<GitSandboxDispatchClaim?> ClaimNextAsync(CancellationToken ct);
    Task<GitSandboxDispatchClaim?> ClaimInspectionAsync(CancellationToken ct);
    Task<bool> CompleteAsync(GitSandboxDispatchClaim claim,
        string state, string outcomeSha256, string outcomeCode,
        CancellationToken ct);
    Task<bool> RequireInspectionAsync(
        GitSandboxDispatchClaim claim, string reason, CancellationToken ct);
    Task<GitSandboxDispatchJob?> GetAsync(
        Guid attemptId, CancellationToken ct);
    Task<IReadOnlyList<GitSandboxDispatchEvent>> EventsAsync(
        Guid attemptId, CancellationToken ct);
}

// The only public method with a sandbox identifier is an INTERNAL .NET
// service: there is no HTTP route or untrusted deserialized user request.
public sealed class GitSandboxDispatchService(
    GitHubCreateOnlyPacketService packets,
    IGitSourceBundleEvidenceStore bundles,
    IIncrementalGitRefReconciliationStore reconciliations,
    IGitSandboxDispatchStore store,
    GitSandboxDispatchOptions options)
{
    public async Task<GitSandboxDispatchJob> EnqueueAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid attemptId, Guid sandboxId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new GitSandboxDispatchException("SandboxDispatchDisabled");
        if (!options.AllowedSandboxIds.Contains(sandboxId))
            throw new GitSandboxDispatchException("SandboxNotAllowlisted");
        if (attemptId == Guid.Empty)
            throw new GitSandboxDispatchException("SandboxDispatchRequestInvalid");

        var verified = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        if (verified.Candidates.Count is < 1 or >
            GitHubCandidatePolicyIntegrity.MaxOnlineCandidates)
            throw new GitSandboxDispatchException("SandboxDispatchRefLimit");
        var packet = verified.Packet;
        var bundle = await bundles.GetAsync(preparationId, planId,
            packet.WorkpackId, ct)
            ?? throw new KeyNotFoundException("SourceBundleNotFound");
        if (!bundle.IsCurrent || bundle.PlanId != planId ||
            bundle.BundleSha256 != packet.BundleSha256 ||
            bundle.ReconciliationSha256 != packet.ReconciliationSha256)
            throw new GitSandboxDispatchException("SandboxBundleChanged");

        // Read the *complete* verified F-032 reconciliation. No ref names
        // enter over HTTP. Snapshot size is bounded to 512 source/target refs.
        var comparisons = new List<GitMirrorRefComparison>();
        string? after = null;
        for (var pageNumber = 0; pageNumber < 12; pageNumber++)
        {
            var page = await reconciliations.PageAsync(preparationId, planId,
                bundle.ReconciliationId, after, 100, ct)
                ?? throw new GitSandboxDispatchException("SandboxReconciliationMissing");
            if (!page.Reconciliation.IsCurrent ||
                page.Reconciliation.ReconciliationSha256 != packet.ReconciliationSha256)
                throw new GitSandboxDispatchException("SandboxReconciliationChanged");
            comparisons.AddRange(page.Items);
            if (comparisons.Count > 2 * GitSandboxDispatchIntegrity.MaxSourceRefs)
                throw new GitSandboxDispatchException("SandboxDispatchRefLimit");
            if (page.NextAfter is null) break;
            if (pageNumber == 11 || string.CompareOrdinal(page.NextAfter, after) <= 0)
                throw new GitSandboxDispatchException("SandboxReconciliationPagination");
            after = page.NextAfter;
        }
        var source = comparisons.Where(x => x.SourceObjectId is not null)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        var target = comparisons.Where(x => x.TargetObjectId is not null)
            .Select(x => new GitMirrorRef(x.Name, x.TargetObjectId!)).ToArray();
        var request = new LocalGitCreateOnlyTransferRequest(
            packet, bundle, sandboxId, source, target, verified.Candidates);
        LocalGitCreateOnlyTransferIntegrity.Verify(request);
        return await store.EnqueueAsync(attemptId, request, ct);
    }
}

public sealed class GitSandboxDispatchProcessor(
    IGitSandboxDispatchStore store,
    ILocalGitCreateOnlyTransferRunner runner)
{
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var claim = await store.ClaimNextAsync(ct);
        if (claim is null) return false;
        try
        {
            // This is the only call that physically writes, and the runner
            // can only write to a preprovisioned local UUID bare repository.
            var receipt = await runner.RunAsync(claim.Request, ct);
            if (!GitSandboxDispatchIntegrity.ValidReceipt(claim, receipt))
                throw new GitSandboxDispatchException("SandboxReceiptMismatch");
            await store.CompleteAsync(claim, GitSandboxDispatchStates.Verified,
                receipt.EvidenceSha256, "LocalGitVerified", ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure after claim is potentially post-transmission.
            // Never claim Queued again; the only next action is a GET/fsck
            // inspection under the F-048 local target lock.
            await store.RequireInspectionAsync(claim,
                "SandboxOutcomeRequiresInspection", CancellationToken.None);
        }
        return true;
    }

    public async Task<bool> InspectOneAsync(CancellationToken ct)
    {
        var claim = await store.ClaimInspectionAsync(ct);
        if (claim is null) return false;
        try
        {
            var observation = await runner.InspectAsync(claim.Request, ct);
            if (observation.SandboxId != claim.Job.SandboxId ||
                observation.ExecutionAuthorized || observation.RemoteWritesPerformed ||
                observation.MayAutomaticallyRetry ||
                !GitSourceBundleStorageCheck.ValidSha(observation.ActualRefsSha256))
                throw new GitSandboxDispatchException("SandboxInspectionInvalid");
            var matched = observation.State == "ExpectedLocalRefsObserved" &&
                observation.MatchingCreatedRefs == claim.Request.CreateRefs.Count;
            await store.CompleteAsync(claim,
                matched ? GitSandboxDispatchStates.ReconciledMatch :
                    GitSandboxDispatchStates.NeedsOperator,
                observation.ActualRefsSha256,
                matched ? "LocalRefsAndObjectsObserved" :
                    "LocalOutcomeDiverged", ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await store.CompleteAsync(claim, GitSandboxDispatchStates.NeedsOperator,
                new string('0', 64), "LocalInspectionFailed",
                CancellationToken.None);
        }
        return true;
    }
}
