namespace GitHubMigrator.Business;

// F-065: strictly aggregate, read-only operational diagnostics.
// No source/target IDs, worker identities, lease tokens, credentials,
// Git refs, paths or permission evidence are exposed by this contract.
public sealed record GitSourceMirrorFleetHealth(
    DateTimeOffset ObservedAtUtc,
    long TotalMirrors,
    long ActiveLeases,
    long ExpiredLeases,
    long ExpiringSoonLeases,
    long VerifiedMirrors,
    long RecoveryRequiredMirrors,
    DateTimeOffset? OldestExpiredAtUtc)
{
    // An expired lease is not proof of a running worker. It requires
    // operator attention, not automatic redispatch or migration approval.
    public bool AttentionRequired =>
        ExpiredLeases > 0 || RecoveryRequiredMirrors > 0;
}

public interface IGitSourceMirrorFleetHealthReader
{
    Task<GitSourceMirrorFleetHealth> ReadAsync(CancellationToken ct);
}
