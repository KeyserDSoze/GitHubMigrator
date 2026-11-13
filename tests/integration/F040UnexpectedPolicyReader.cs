using GitHubMigrator.Business;

namespace GitHubMigrator.Integration.Tests;

// An existing F-040 intention should replay its immutable policy proof
// without contacting GitHub again. This stub must never be invoked.
internal sealed class F040UnexpectedPolicyReader : IGitHubImportPolicyReader
{
    public int Calls { get; private set; }

    public Task<GitHubImportPolicyObservation> InspectAsync(
        string organization, string repository, string token, CancellationToken ct)
    {
        Calls++;
        throw new InvalidOperationException(
            "A certified import-intent replay must not re-read GitHub policy.");
    }
}
