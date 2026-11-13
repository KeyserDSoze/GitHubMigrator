using System.Security.Cryptography;
using System.Text;

namespace GitHubMigrator.Infrastructure.GitHub;

// git's object id of a file: what the contents API answers as `sha` for exactly these bytes. Computing it here is how a blob id the destination reports is checked
// against the bytes that were read, instead of being believed.
internal static class GitBlob
{
    public static string Sha(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
