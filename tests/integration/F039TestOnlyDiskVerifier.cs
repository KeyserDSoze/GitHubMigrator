using GitHubMigrator.Business;

namespace GitHubMigrator.Integration.Tests;

// Used ONLY for the F-039 PostgreSQL transaction/immutability contract test,
// which seeds a synthetic F-035 bundle. The real API has the actual F-038
// on-disk SHA-256 verifier; see LocalGitSourceBundleAcquirerTests.
internal sealed class F039TestOnlyDiskVerifier : IGitSourceBundleArtifactVerifier
{
    public Task<GitSourceBundleStorageCheck> VerifyAsync(
        GitSourceBundleView evidence, CancellationToken ct)
    {
        return Task.FromResult(new GitSourceBundleStorageCheck(
            evidence.WorkpackId, evidence.BundleSha256, evidence.BundleBytes,
            DateTimeOffset.UtcNow, evidence.IsCurrent, false));
    }
}
