using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Domain.Tests;

public sealed class ProtectedCredentialStorageTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Development_credentials_are_encrypted_and_can_be_read_privately()
    {
        await using var db = Database();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var store = new PostgresEncryptedConnectionSecretStore(db, key);
        await store.SaveAsync("connection-test", "fake-token-42", CancellationToken.None);
        var record = await db.ConnectionSecrets.SingleAsync();
        Assert.NotEqual("fake-token-42", Encoding.UTF8.GetString(record.Ciphertext));
        Assert.Equal(12, record.Nonce.Length);
        Assert.Equal(16, record.Tag.Length);
        Assert.Equal("fake-token-42", await store.ReadAsync("connection-test", CancellationToken.None));
    }

    [Fact]
    public async Task Every_replacement_uses_a_fresh_nonce_and_invalid_key_is_rejected()
    {
        await using var db = Database();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var store = new PostgresEncryptedConnectionSecretStore(db, key);
        await store.SaveAsync("connection-test", "same-token", CancellationToken.None);
        var firstNonce = (await db.ConnectionSecrets.SingleAsync()).Nonce.ToArray();
        await store.SaveAsync("connection-test", "same-token", CancellationToken.None);
        Assert.NotEqual(firstNonce, (await db.ConnectionSecrets.SingleAsync()).Nonce);
        Assert.Throws<InvalidOperationException>(() => new PostgresEncryptedConnectionSecretStore(db, "too-short"));
    }

    [Fact]
    public async Task Tampered_ciphertext_fails_aes_gcm_authentication()
    {
        await using var db = Database();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var store = new PostgresEncryptedConnectionSecretStore(db, key);
        await store.SaveAsync("connection-test", "fake-token", CancellationToken.None);
        var record = await db.ConnectionSecrets.SingleAsync();
        var tampered = record.Ciphertext.ToArray();
        tampered[0] ^= 0x01;
        record.Ciphertext = tampered;
        db.Entry(record).Property(x => x.Ciphertext).IsModified = true;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            store.ReadAsync("connection-test", CancellationToken.None));
    }
}
