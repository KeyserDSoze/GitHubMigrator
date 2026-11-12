using System.Security.Cryptography;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #133: isolated owner migration uses the real operator SQL. The
// created role is NOLOGIN, with no memberships, and cannot authorize
// inserts or future F-061 generation. Restore every object owner.
public sealed class PostgresF061SecondReviewOwnerBoundaryTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    private static readonly string[] JournalTables =
    [
        "git_later_checkpoint_mirror_second_reviews",
        "git_later_checkpoint_mirror_second_review_events",
        "git_later_checkpoint_mirror_second_review_session_consumptions"
    ];

    [Fact]
    public async Task One_shot_no_login_owner_is_idempotent_and_separate_from_runtime()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerName = "f061_owner_" + Guid.NewGuid().ToString("N");
        var sql = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "postgres_f061_second_review_owner.sql"));
        var originalOwner = await db.Database.SqlQueryRaw<string>("""
            SELECT pg_catalog.pg_get_userbyid(c.relowner) AS "Value"
            FROM pg_catalog.pg_class c
            WHERE c.oid =
                'public.git_later_checkpoint_mirror_second_reviews'::regclass
            """).SingleAsync();
        // #144: the reusable PG17 service database grants database
        // TEMPORARY to PUBLIC by default. The runtime-only security
        // guard now correctly rejects it, so provision the disposable
        // CI database explicitly rather than weakening the guard.
        // Snapshot the exact original PUBLIC grant for restoration.
        var databaseName = new NpgsqlConnectionStringBuilder(
            db.Database.GetDbConnection().ConnectionString).Database;
        var databaseIdentifier = "\"" + databaseName.Replace("\"", "\"\"") + "\"";
        var publicHadTemporary = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_database d
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) acl
                WHERE d.datname = CURRENT_DATABASE()
                  AND acl.grantee = 0::oid
                  AND acl.privilege_type = 'TEMPORARY'
            ) AS "Value"
            """).SingleAsync();
        // The shared ephemeral CI database is bootstrapped as superuser;
        // use a fresh, independent NOLOGIN principal, not SET ROLE.
        Assert.NotEqual(ownerName, originalOwner);
        try
        {
            // Test-only privilege provisioning: never silently modify
            // PUBLIC ACLs in an actual shared/production environment.
            await db.Database.ExecuteSqlRawAsync(
                $"REVOKE TEMPORARY ON DATABASE {databaseIdentifier} FROM PUBLIC");
            // #143: independently test the THIRD security boundary,
            // the exact privileged NOLOGIN owner operator. The other
            // two SQL guards are exercised by the restricted-runtime
            // suite. PostgreSQL's ordinary ENABLE TRIGGER gives 'O',
            // which can be skipped in replica sessions. The owner
            // migration must refuse each of the three individual
            // downgraded journal triggers, never silently normalize it.
            // Every downgrade is uncommitted and rolled back, so other
            // PG17 integration contexts never observe weakened fences.
            foreach (var (table, trigger) in new[]
            {
                ("git_later_checkpoint_mirror_second_reviews",
                    "trg_f061v2e_second_review_closed"),
                ("git_later_checkpoint_mirror_second_review_events",
                    "trg_f061v2e_second_review_event_closed"),
                ("git_later_checkpoint_mirror_second_review_session_consumptions",
                    "trg_f061v2e_second_review_session_closed")
            })
            {
                await using var negative = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE public.{table} ENABLE TRIGGER {trigger}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.f061_review_owner', {ownerName}, true)");
                await using var command = db.Database.GetDbConnection()
                    .CreateCommand();
                command.Transaction = negative.GetDbTransaction();
                command.CommandText = sql;
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal("P0001", refused.SqlState);
                Assert.Contains("F-061 deny-all trigger fences incomplete",
                    refused.MessageText);
                await negative.RollbackAsync();
            }
            // The abort never installs even an inert owner principal.
            Assert.False(await db.Database.SqlQueryRaw<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_roles
                    WHERE rolname = '{ownerName}'
                ) AS "Value"
                """).SingleAsync());

            for (var i = 0; i < 2; i++)
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.f061_review_owner', {ownerName}, true)");
                // ADO.NET preserves SQL regex braces; EF RawSQL does not.
                await using (var command =
                    db.Database.GetDbConnection().CreateCommand())
                {
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }

            var validated = await db.Database.SqlQueryRaw<bool>($"""
                SELECT
                  (SELECT NOT rolcanlogin AND NOT rolsuper AND
                              NOT rolinherit AND NOT rolcreaterole
                   FROM pg_catalog.pg_roles
                   WHERE rolname = '{ownerName}')
                  AND (SELECT count(*) = 3
                       FROM pg_catalog.pg_class c
                       WHERE c.oid IN (
                         'public.git_later_checkpoint_mirror_second_reviews'::regclass,
                         'public.git_later_checkpoint_mirror_second_review_events'::regclass,
                         'public.git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                       )
                       AND pg_catalog.pg_get_userbyid(c.relowner) = '{ownerName}')
                  AND (SELECT pg_catalog.pg_get_userbyid(p.proowner) = '{ownerName}'
                       FROM pg_catalog.pg_proc p
                       WHERE p.oid =
                         'public.git_later_checkpoint_mirror_second_review_deny()'::regprocedure)
                  AND (SELECT count(*) = 3
                       FROM pg_catalog.pg_trigger t
                       JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
                       WHERE c.oid IN (
                         'public.git_later_checkpoint_mirror_second_reviews'::regclass,
                         'public.git_later_checkpoint_mirror_second_review_events'::regclass,
                         'public.git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                       )
                       AND NOT t.tgisinternal
                       AND t.tgenabled = 'A'
                       AND t.tgfoid =
                         'public.git_later_checkpoint_mirror_second_review_deny()'::regprocedure
                       AND (t.tgtype::integer & 31) = 31)
                  AS "Value"
                """).SingleAsync();
            Assert.True(validated);

            var login = "f061_owner_probe_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE ROLE {login} LOGIN PASSWORD '{password}' " +
                "NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT " +
                "NOREPLICATION NOBYPASSRLS");
            try
            {
                var restricted = new NpgsqlConnectionStringBuilder(
                    db.Database.GetDbConnection().ConnectionString)
                {
                    Username = login,
                    Password = password,
                    Pooling = false
                };
                await using var caller = new NpgsqlConnection(
                    restricted.ConnectionString);
                await caller.OpenAsync();
                await using (var verifyDb = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(restricted.ConnectionString).Options))
                    await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                        verifyDb);
                await using var attempt = new NpgsqlCommand(
                    "INSERT INTO public.git_later_checkpoint_mirror_second_reviews " +
                    "DEFAULT VALUES", caller);
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => attempt.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
                await using var escalate = new NpgsqlCommand(
                    "SET ROLE " + ownerName, caller);
                var denied = await Assert.ThrowsAsync<PostgresException>(
                    () => escalate.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"DROP ROLE IF EXISTS {login}");
            }
        }
        finally
        {
            try
            {
                // Only change back the exact three fixtures + deny function
                // before dropping the disposable NOLOGIN owner. In a failed
                // transaction the original owner is already unchanged.
                foreach (var name in JournalTables)
                    await db.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE public.{name} OWNER TO {originalOwner}");
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER FUNCTION public.git_later_checkpoint_mirror_second_review_deny() " +
                    $"OWNER TO {originalOwner}");
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP ROLE IF EXISTS {ownerName}");
            }
            finally
            {
                // Even a cleanup failure must not strand the shared CI
                // test database with its PUBLIC ACL silently changed.
                if (publicHadTemporary)
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT TEMPORARY ON DATABASE {databaseIdentifier} TO PUBLIC");
            }
        }
    }
}
