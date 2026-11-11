using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// #128: idempotent repair of the historical F-061 CHECK expressions
// serialized from NON-interpolated C# raw SQL as {{64}} (instead of
// PostgreSQL's {64}). No row/lease/event is modified, and only the
// exact F-061 CHECK constraints are affected. Existing NOT VALID
// status is preserved by pg_get_constraintdef in the replacement.
public static class GitIncrementalLaterCheckpointMirrorRegexConstraintRepairSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            DO $f061v2_sha_regex_fix$
            DECLARE
                existing record;
            BEGIN
                FOR existing IN
                    SELECT
                        c.conrelid::regclass AS table_name,
                        c.conname AS constraint_name,
                        pg_get_constraintdef(c.oid) AS definition
                    FROM pg_constraint c
                    WHERE c.contype = 'c'
                        AND c.conrelid IN (
                            'git_later_checkpoint_mirror_leases'::regclass,
                            'git_later_checkpoint_mirror_lease_events'::regclass,
                            'git_later_checkpoint_mirror_verified_evidence'::regclass,
                            'git_later_checkpoint_mirror_recovery_decisions'::regclass,
                            'git_later_checkpoint_mirror_recovery_decision_events'::regclass,
                            'git_later_checkpoint_mirror_isolation_intents'::regclass,
                            'git_later_checkpoint_mirror_isolation_intent_events'::regclass,
                            'git_later_checkpoint_mirror_isolation_receipts'::regclass,
                            'git_later_checkpoint_mirror_isolation_receipt_events'::regclass,
                            'git_later_checkpoint_mirror_generation_assessments'::regclass,
                            'git_later_checkpoint_mirror_generation_assessment_events'::regclass)
                        AND position(
                            '{{64}}' in pg_get_constraintdef(c.oid)) > 0
                LOOP
                    -- The DO block is one PostgreSQL transaction; if an
                    -- upgraded CHECK cannot be created, the preceding
                    -- DROP rolls back. Historical rows cannot be made
                    -- mutable or bypassed by a partial upgrade.
                    EXECUTE format(
                        'ALTER TABLE %s DROP CONSTRAINT %I',
                        existing.table_name, existing.constraint_name);
                    EXECUTE format(
                        'ALTER TABLE %s ADD CONSTRAINT %I %s',
                        existing.table_name, existing.constraint_name,
                        replace(existing.definition, '{{64}}', '{64}'));
                END LOOP;
            END $f061v2_sha_regex_fix$;
            """, ct);
    }
}
