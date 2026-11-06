using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-054 is an immutable observation journal, NOT a remote send queue.
// A verified tip is not proof that an operation sent data or finished.
public static class GitHubRemoteReadbackStates
{
    public const string ExactTipEvidenceOnly = "ExactTipEvidenceOnly";
    public const string OperatorReviewRequired = "OperatorReviewRequired";
}

public sealed class GitHubRemoteReadbackException(string code)
    : Exception("GitHub read-only remote reconciliation refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemoteReadbackEvidence(
    Guid GrantId, long RepositoryId, Guid ObserverId,
    IReadOnlyList<GitHubObservedRef> Baseline,
    IReadOnlyList<GitMirrorRef> CreateCandidates,
    GitHubRemoteTipSnapshot Snapshot);

public sealed record GitHubRemoteReadbackDecision(
    string State, string BaselineSha256, string CandidateSetSha256,
    string RefSetSha256, string TipEvidenceSha256,
    string AssessmentSha256, string JournalEvidenceSha256,
    DateTimeOffset ObservedAtUtc);

public sealed record GitHubRemoteReadbackStatus(
    Guid Id, Guid GrantId, long RepositoryId,
    string State, string BaselineSha256, string CandidateSetSha256,
    string RefSetSha256, string TipEvidenceSha256,
    string AssessmentSha256, string JournalEvidenceSha256,
    Guid ObservedBy, DateTimeOffset ObservedAtUtc,
    DateTimeOffset RecordedAtUtc, bool TransportAttempted,
    bool RemoteWritesAuthorized, bool MayAutomaticallyRetry);

public sealed record GitHubRemoteReadbackEvent(
    long Id, Guid ReadbackId, string Type,
    Guid ActorId, DateTimeOffset OccurredAtUtc);

public interface IGitHubRemoteReadbackJournal
{
    Task<GitHubRemoteReadbackStatus> RecordAsync(
        GitHubRemoteReadbackEvidence evidence, CancellationToken ct);
    Task<GitHubRemoteReadbackStatus?> GetAsync(
        Guid grantId, long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteReadbackEvent>> EventsAsync(
        Guid grantId, long repositoryId, CancellationToken ct);
}

public static class GitHubRemoteReadbackIntegrity
{
    public static readonly TimeSpan MaxObservationAge = TimeSpan.FromSeconds(30);

    // Validate the actual F-053 observation/assessment and bind it to the
    // F-052 candidate+baseline SHA, not a caller supplied "success" flag.
    public static GitHubRemoteReadbackDecision Verify(
        GitHubRemoteReadbackEvidence evidence, DateTimeOffset now)
    {
        if (evidence is null || evidence.GrantId == Guid.Empty ||
            evidence.RepositoryId <= 0 || evidence.ObserverId == Guid.Empty ||
            evidence.Snapshot is null || evidence.Snapshot.RemoteRefs is null ||
            evidence.Snapshot.TipReadback is null)
            throw new GitHubRemoteReadbackException("RemoteReadbackInputInvalid");

        var remote = evidence.Snapshot.RemoteRefs;
        var tip = evidence.Snapshot.TipReadback;
        if (remote.ObservedAtUtc < now - MaxObservationAge ||
            remote.ObservedAtUtc > now.AddSeconds(5) ||
            tip.ObservedAtUtc < remote.ObservedAtUtc ||
            tip.ObservedAtUtc > now.AddSeconds(5))
            throw new GitHubRemoteReadbackException("RemoteReadbackObservationStale");

        GitHubRemotePostSendObjectAssessment assessment;
        IReadOnlyList<GitMirrorRef> baseline;
        IReadOnlyList<GitMirrorRef> creates;
        try
        {
            // Preserve annotated tag *object types* from the original full
            // F-051 baseline, rather than guessing that tags are commits.
            var canonicalBaseline = GitHubRefObservationIntegrity.Seal(
                remote.Organization, remote.Repository,
                remote.ObservedAtUtc, evidence.Baseline);
            if (!canonicalBaseline.Refs.SequenceEqual(evidence.Baseline))
                throw new GitHubRemoteReadbackException(
                    "RemoteReadbackInputInvalid");
            baseline = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                evidence.Baseline.Select(x =>
                    new GitMirrorRef(x.Name, x.ObjectId)).ToArray());
            creates = GitHubRemoteTipIntegrity.CanonicalCandidates(
                evidence.CreateCandidates);
            assessment = GitHubRemoteTipIntegrity.Inspect(
                baseline, creates, remote, tip);
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubRemoteReadbackException("RemoteReadbackInputInvalid");
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubRemoteReadbackException("RemoteReadbackInputInvalid");
        }
        catch (GitHubRemoteTipException)
        {
            throw new GitHubRemoteReadbackException("RemoteReadbackEvidenceInvalid");
        }
        catch (GitHubRemoteTestGrantException)
        {
            throw new GitHubRemoteReadbackException("RemoteReadbackEvidenceInvalid");
        }

        var baselineSha = GitHubRemoteRefFenceIntegrity.Fingerprint(
            evidence.Baseline);

        var candidatesSha = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", creates);
        var state = assessment.State == "ExactRemoteRefsAndTipObjectsObserved"
            ? GitHubRemoteReadbackStates.ExactTipEvidenceOnly
            : GitHubRemoteReadbackStates.OperatorReviewRequired;

        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f054-immutable-remote-readback-v1",
                evidence.GrantId.ToString("D"), evidence.RepositoryId,
                state, baselineSha, candidatesSha,
                assessment.RefSetSha256, assessment.TipEvidenceSha256,
                assessment.EvidenceSha256,
                tip.ObservedAtUtc.ToUnixTimeMilliseconds(),
                false, false, false
            }))));
        return new(state, baselineSha, candidatesSha,
            assessment.RefSetSha256, assessment.TipEvidenceSha256,
            assessment.EvidenceSha256, digest, tip.ObservedAtUtc);
    }
}
