using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class IdentityMappingDraftRecord
{
    public Guid SnapshotId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public string TargetOrganization { get; set; } = "";
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string MappingsJson { get; set; } = "[]";
}

public sealed class PostgresIdentityMappingDraftRepository(AppDbContext db) : IIdentityMappingDraftRepository
{
    public async Task<IdentityMappingDraft?> GetAsync(Guid snapshotId, Guid destinationId, CancellationToken ct)
    {
        var row = await db.IdentityMappingDrafts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SnapshotId == snapshotId && x.DestinationConnectionId == destinationId, ct);
        return row is null ? null : new IdentityMappingDraft(row.SnapshotId, row.DestinationConnectionId,
            row.TargetOrganization, row.Revision, row.UpdatedAtUtc,
            JsonSerializer.Deserialize<IdentityMapping[]>(row.MappingsJson) ?? []);
    }

    public async Task<bool> SaveAsync(IdentityMappingDraft draft, int expectedRevision, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(draft.Mappings);
        // Expected revision zero permits ONLY new drafts. Conditional UPDATE is atomic on PostgreSQL
        // and prevents an older tab/session from replacing mappings saved by another administrator.
        if (expectedRevision == 0)
        {
            var created = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO identity_mapping_drafts (
                    snapshot_id, destination_connection_id, target_organization,
                    revision, updated_at_utc, mappings_json)
                VALUES ({draft.SnapshotId}, {draft.DestinationConnectionId}, {draft.TargetOrganization},
                    {draft.Revision}, {draft.UpdatedAtUtc}, {json}::jsonb)
                ON CONFLICT (snapshot_id, destination_connection_id) DO NOTHING
                """, ct);
            return created == 1;
        }
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity_mapping_drafts SET
                revision = {draft.Revision},
                updated_at_utc = {draft.UpdatedAtUtc},
                mappings_json = {json}::jsonb
            WHERE snapshot_id = {draft.SnapshotId}
                AND destination_connection_id = {draft.DestinationConnectionId}
                AND revision = {expectedRevision}
            """, ct);
        return updated == 1;
    }
}
