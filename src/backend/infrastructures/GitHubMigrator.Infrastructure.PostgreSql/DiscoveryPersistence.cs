using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class DiscoveryInventoryRecord
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTimeOffset CollectedAtUtc { get; set; }
    public string Json { get; set; } = "{}";
}

public sealed class PostgresDiscoveryRepository(AppDbContext db) : IDiscoveryRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task SaveAsync(DiscoveryInventory inventory, CancellationToken ct)
    {
        // Immutable and idempotent: replay of a job-owned snapshot ID never
        // overwrites a committed scan, even after an abrupt worker process death.
        // PostgreSQL's PK handles concurrent inserts without EF tracking residue.
        var json = JsonSerializer.Serialize(inventory, Options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO migration_discoveries (id, connection_id, collected_at_utc, inventory)
            VALUES ({inventory.Id}, {inventory.ConnectionId}, {inventory.CollectedAtUtc}, {json}::jsonb)
            ON CONFLICT (id) DO NOTHING
            """, ct);
    }

    public async Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct)
    {
        var record = await db.Discoveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return record is null ? null : JsonSerializer.Deserialize<DiscoveryInventory>(record.Json, Options);
    }

    public async Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct)
    {
        // Bounded list to avoid an unbounded metadata read; use server pagination later.
        var rows = await db.Discoveries.AsNoTracking().OrderByDescending(x => x.CollectedAtUtc)
            .Take(25).ToListAsync(ct);
        return rows.Select(x => JsonSerializer.Deserialize<DiscoveryInventory>(x.Json, Options)!)
            .ToArray();
    }
}
