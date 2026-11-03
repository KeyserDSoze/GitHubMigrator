using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class MigrationPlanDraftRecord
{
    public Guid SnapshotId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public string TargetOrganization { get; set; } = "";
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string RepositoriesJson { get; set; } = "[]";
}

public sealed class PostgresMigrationPlanDraftRepository(AppDbContext db) : IMigrationPlanDraftRepository
{
    public async Task<MigrationPlanDraft?> GetAsync(Guid snapshotId, Guid destinationConnectionId, CancellationToken ct)
    {
        var row = await db.MigrationPlanDrafts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId &&
                x.DestinationConnectionId == destinationConnectionId, ct);
        return row is null ? null : new(row.SnapshotId, row.DestinationConnectionId,
            row.TargetOrganization, row.Revision, row.UpdatedAtUtc,
            JsonSerializer.Deserialize<PlanSelectionInput[]>(row.RepositoriesJson) ?? []);
    }

    public async Task<bool> SaveAsync(MigrationPlanDraft draft, int expectedRevision, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(draft.Repositories);
        // PostgreSQL atomic CAS prevents stale browser tabs/admins overwriting newer changes.
        if (expectedRevision == 0)
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO migration_plan_drafts (
                    snapshot_id, destination_connection_id, target_organization,
                    revision, updated_at_utc, repositories_json)
                VALUES ({draft.SnapshotId}, {draft.DestinationConnectionId}, {draft.TargetOrganization},
                    {draft.Revision}, {draft.UpdatedAtUtc}, {json}::jsonb)
                ON CONFLICT (snapshot_id, destination_connection_id) DO NOTHING
                """, ct);
            return inserted == 1;
        }
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE migration_plan_drafts SET
                revision = {draft.Revision},
                updated_at_utc = {draft.UpdatedAtUtc},
                repositories_json = {json}::jsonb
            WHERE snapshot_id = {draft.SnapshotId}
                AND destination_connection_id = {draft.DestinationConnectionId}
                AND revision = {expectedRevision}
            """, ct);
        return updated == 1;
    }
}
