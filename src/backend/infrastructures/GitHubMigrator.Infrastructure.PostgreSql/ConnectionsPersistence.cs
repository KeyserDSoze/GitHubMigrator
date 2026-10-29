using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class ConnectionRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public ConnectionDirection Direction { get; set; }
    public string Organization { get; set; } = "";
    public string SecretReference { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ValidatedAtUtc { get; set; }
    public string ValidationCode { get; set; } = "";
    public string? AuthenticatedAs { get; set; }
    public string PermissionAssessment { get; set; } = "";
}

public sealed class EncryptedSecretRecord
{
    public string Reference { get; set; } = "";
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
}

public sealed class PostgresConnectionRepository(AppDbContext db) : IConnectionRepository
{
    public async Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
        (await db.Connections.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync(ct))
            .Select(Map).ToArray();

    public async Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct)
    {
        var record = await db.Connections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return record is null ? null : Map(record);
    }

    public async Task SaveAsync(MigrationConnection connection, CancellationToken ct)
    {
        var stored = await db.Connections.FindAsync([connection.Id], ct);
        if (stored is null) { stored = new ConnectionRecord { Id = connection.Id }; db.Connections.Add(stored); }
        stored.Name = connection.Name;
        stored.ProviderKey = connection.ProviderKey;
        stored.Direction = connection.Direction;
        stored.Organization = connection.Organization;
        stored.SecretReference = connection.SecretReference;
        stored.CreatedAtUtc = connection.CreatedAtUtc;
        stored.ValidatedAtUtc = connection.ValidatedAtUtc;
        stored.ValidationCode = connection.ValidationCode;
        stored.AuthenticatedAs = connection.AuthenticatedAs;
        stored.PermissionAssessment = connection.PermissionAssessment;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await db.Connections.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    }

    private static MigrationConnection Map(ConnectionRecord x) =>
        new(x.Id, x.Name, x.ProviderKey, x.Direction, x.Organization, x.SecretReference,
            x.CreatedAtUtc, x.ValidatedAtUtc, x.ValidationCode, x.AuthenticatedAs, x.PermissionAssessment);
}

// Development-only: AES-256-GCM, one random nonce per write. Key comes from ignored .env.local.
// Azure uses Key Vault instead, so no PAT is stored in cloud PostgreSQL.
public sealed class PostgresEncryptedConnectionSecretStore : IConnectionSecretStore
{
    private readonly AppDbContext _db;
    private readonly byte[] _key;
    public PostgresEncryptedConnectionSecretStore(AppDbContext db, string base64Key)
    {
        _db = db;
        try { _key = Convert.FromBase64String(base64Key); }
        catch (FormatException) { throw new InvalidOperationException("Secrets:LocalEncryptionKey must be base64."); }
        if (_key.Length != 32) throw new InvalidOperationException("Secrets:LocalEncryptionKey requires 32 random bytes.");
    }

    public async Task SaveAsync(string reference, string personalAccessToken, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var content = System.Text.Encoding.UTF8.GetBytes(personalAccessToken);
        var ciphertext = new byte[content.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(nonce, content, ciphertext, tag, System.Text.Encoding.UTF8.GetBytes(reference));
        CryptographicOperations.ZeroMemory(content);
        var record = await _db.ConnectionSecrets.FindAsync([reference], ct);
        if (record is null)
        {
            record = new EncryptedSecretRecord { Reference = reference };
            _db.ConnectionSecrets.Add(record);
        }
        record.Nonce = nonce; record.Ciphertext = ciphertext; record.Tag = tag;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> ReadAsync(string reference, CancellationToken ct)
    {
        var record = await _db.ConnectionSecrets.AsNoTracking().FirstOrDefaultAsync(x => x.Reference == reference, ct);
        if (record is null) return null;
        var plaintext = new byte[record.Ciphertext.Length];
        using (var aes = new AesGcm(_key, 16))
            aes.Decrypt(record.Nonce, record.Ciphertext, record.Tag, plaintext,
                System.Text.Encoding.UTF8.GetBytes(reference));
        try { return System.Text.Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task DeleteAsync(string reference, CancellationToken ct) =>
        await _db.ConnectionSecrets.Where(x => x.Reference == reference).ExecuteDeleteAsync(ct);
}
