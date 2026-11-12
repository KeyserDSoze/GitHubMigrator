using System.Security.Cryptography;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #133: install the EXACT operator pilot SQL in an isolated real PG17
// environment for a disposable NOSUPERUSER LOGIN, and verify separate
// authentication/Identity CRUD without broad F-061 journal grants.
// No production runtime user or HTTP bootstrap account is changed.
public sealed class PostgresRestrictedRuntimeIdentityGrantTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task Identity_only_grants_are_idempotent_and_do_not_open_F061_journals()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var login = "f061_pilot_" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var sqlPath = Path.Combine(
            AppContext.BaseDirectory, "postgres_runtime_identity_grants.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        Assert.Contains("GRANT SELECT ON TABLE", sql);

        await db.Database.ExecuteSqlRawAsync(
            $"CREATE ROLE {login} LOGIN PASSWORD '{password}' " +
            "NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT " +
            "NOREPLICATION NOBYPASSRLS");
        try
        {
            // psql's production wrapper uses --single-transaction plus
            // SET LOCAL; reproduce that atomic contract with the same SQL.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                await db.Database.ExecuteSqlRawAsync(sql);
                await tx.CommitAsync();
            }

            var connection = new NpgsqlConnectionStringBuilder(
                db.Database.GetDbConnection().ConnectionString)
            {
                Username = login,
                Password = password,
                Pooling = false
            };
            await using var caller = new NpgsqlConnection(connection.ConnectionString);
            await caller.OpenAsync();
            async Task<T> Scalar<T>(string statement)
            {
                await using var cmd = new NpgsqlCommand(statement, caller);
                return (T)(await cmd.ExecuteScalarAsync())!;
            }
            Assert.Equal(login, await Scalar<string>("SELECT SESSION_USER"));
            Assert.True(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public."AspNetUsers"', 'SELECT')
                """));
            Assert.True(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public."AspNetUsers"', 'UPDATE')
                """));
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public."AspNetUsers"', 'DELETE')
                """));
            Assert.True(await Scalar<long>(
                """SELECT count(*)::bigint FROM public."AspNetUsers" """) >= 1L);
            await using (var safeUpdate = new NpgsqlCommand("""
                UPDATE public."AspNetUsers" SET "AccessFailedCount" = "AccessFailedCount"
                WHERE false
                """, caller))
                Assert.Equal(0, await safeUpdate.ExecuteNonQueryAsync());

            await using (var runtimeDb = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(connection.ConnectionString).Options))
                await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(runtimeDb);

            foreach (var table in new[] {
                "git_later_checkpoint_mirror_second_reviews",
                "git_later_checkpoint_mirror_second_review_events",
                "git_later_checkpoint_mirror_second_review_session_consumptions"
            })
            {
                // Fixed server-owned identifiers, never request input.
                Assert.False(await Scalar<bool>(
                    "SELECT pg_catalog.has_table_privilege(" +
                    "CURRENT_USER, 'public." + table + "', " +
                    "'INSERT,UPDATE,DELETE')"));
                await using var write = new NpgsqlCommand(
                    "INSERT INTO public." + table + " DEFAULT VALUES", caller);
                var rejected = await Assert.ThrowsAsync<PostgresException>(
                    () => write.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                    rejected.SqlState);
            }
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_schema_privilege(
                    CURRENT_USER, 'public', 'CREATE')
                """));
        }
        finally
        {
            // Remove test-only GRANT dependencies before deleting LOGIN.
            // No application tables or normal platform users are deleted.
            await db.Database.ExecuteSqlRawAsync($"DROP OWNED BY {login}");
            await db.Database.ExecuteSqlRawAsync($"DROP ROLE IF EXISTS {login}");
        }
    }
}
