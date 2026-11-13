using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class AiConnectionRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? ApiVersion { get; set; }
    public string TokenParameter { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public double? Temperature { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MaxInputBytes { get; set; }
    public string SecretReference { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public string VerificationCode { get; set; } = "";
    public string? VerifiedModel { get; set; }
    public bool SendSourceContent { get; set; }
    public DateTimeOffset? ConsentAtUtc { get; set; }
    public string? ConsentBy { get; set; }
}

public sealed class PostgresAiConnectionRepository(AppDbContext db) : IAiConnectionRepository
{
    // Like the migration connections inventory, the list does not need the credential-store lookup key.
    public async Task<IReadOnlyList<AiConnection>> ListAsync(CancellationToken ct)
    {
        var rows = await db.AiConnections.AsNoTracking().OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id, x.Name, x.Provider, x.Endpoint, x.Model, x.Auth, x.ApiVersion, x.TokenParameter, x.MaxOutputTokens,
                x.Temperature, x.TimeoutSeconds, x.MaxInputBytes, x.CreatedAtUtc, x.VerifiedAtUtc, x.VerificationCode,
                x.VerifiedModel, x.SendSourceContent, x.ConsentAtUtc, x.ConsentBy
            }).ToListAsync(ct);
        return rows.Select(x => new AiConnection(x.Id, x.Name, Parse<AiProvider>(x.Provider), x.Endpoint, x.Model, Parse<AiAuthMode>(x.Auth),
            x.ApiVersion, Parse<TokenLimitParameter>(x.TokenParameter), x.MaxOutputTokens, x.Temperature, x.TimeoutSeconds, x.MaxInputBytes,
            string.Empty, x.CreatedAtUtc, x.VerifiedAtUtc, x.VerificationCode, x.VerifiedModel, x.SendSourceContent, x.ConsentAtUtc, x.ConsentBy)).ToArray();
    }

    public async Task<AiConnection?> GetAsync(Guid id, CancellationToken ct)
    {
        var x = await db.AiConnections.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return x is null ? null : new AiConnection(x.Id, x.Name, Parse<AiProvider>(x.Provider), x.Endpoint, x.Model, Parse<AiAuthMode>(x.Auth),
            x.ApiVersion, Parse<TokenLimitParameter>(x.TokenParameter), x.MaxOutputTokens, x.Temperature, x.TimeoutSeconds, x.MaxInputBytes,
            x.SecretReference, x.CreatedAtUtc, x.VerifiedAtUtc, x.VerificationCode, x.VerifiedModel, x.SendSourceContent, x.ConsentAtUtc, x.ConsentBy);
    }

    public async Task SaveAsync(AiConnection c, CancellationToken ct)
    {
        var stored = await db.AiConnections.FindAsync([c.Id], ct);
        if (stored is null) { stored = new AiConnectionRecord { Id = c.Id }; db.AiConnections.Add(stored); }
        stored.Name = c.Name;
        stored.Provider = c.Provider.ToString();
        stored.Endpoint = c.Endpoint;
        stored.Model = c.Model;
        stored.Auth = c.Auth.ToString();
        stored.ApiVersion = c.ApiVersion;
        stored.TokenParameter = c.TokenParameter.ToString();
        stored.MaxOutputTokens = c.MaxOutputTokens;
        stored.Temperature = c.Temperature;
        stored.TimeoutSeconds = c.TimeoutSeconds;
        stored.MaxInputBytes = c.MaxInputBytes;
        stored.SecretReference = c.SecretReference;
        stored.CreatedAtUtc = c.CreatedAtUtc;
        stored.VerifiedAtUtc = c.VerifiedAtUtc;
        stored.VerificationCode = c.VerificationCode;
        stored.VerifiedModel = c.VerifiedModel;
        stored.SendSourceContent = c.SendSourceContent;
        stored.ConsentAtUtc = c.ConsentAtUtc;
        stored.ConsentBy = c.ConsentBy;
        await db.SaveChangesAsync(ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => db.AiConnections.Where(x => x.Id == id).ExecuteDeleteAsync(ct);

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new InvalidOperationException("Unknown stored AI connection value.");
}
