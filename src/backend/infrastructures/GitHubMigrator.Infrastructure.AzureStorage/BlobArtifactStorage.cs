using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.AzureStorage;

public sealed class BlobArtifactStorage(BlobContainerClient container) : IArtifactStorage
{
    public async Task PutAsync(string objectKey, Stream contents, string contentType, CancellationToken cancellationToken)
    {
        ValidateKey(objectKey);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient(objectKey).UploadAsync(contents,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public async Task<Stream> ReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        ValidateKey(objectKey);
        return await container.GetBlobClient(objectKey).OpenReadAsync(cancellationToken: cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid artifact key.", nameof(key));
    }
}
