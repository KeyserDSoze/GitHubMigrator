using System.Security.Cryptography;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #133: exercise REAL PostgreSQL privileges through a SEPARATE LOGIN
// connection, not SET ROLE on the application's superuser connection.
// The disposable test principal has no superuser, owner or writer grant.
// This is a baseline negative regression, NOT deployment of a restricted
// runtime user or permission to enable the #130 decision writer.
public sealed class PostgresGitIncrementalLaterCheckpointMirrorRoleBoundaryTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F061v2e_unprivileged_login_cannot_bypass_second_review_triggers()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var isBootstrapSuperuser = await owner.Database.SqlQueryRaw<bool>("""
            SELECT rolsuper AS "Value" FROM pg_catalog.pg_roles
            WHERE rolname = current_user
            """).SingleAsync();
        // The existing integration database bootstraps its schemas with
        // the PostgreSQL image's superuser. A future #133 migration must
        // supply an explicit distinct schema-setup role instead.
        Assert.True(isBootstrapSuperuser,
            "#133 fixture must explicitly distinguish bootstrap and runtime DB roles");
        // Runtime-only API startup must refuse this SAME privileged
        // bootstrap account even when every deny-all trigger exists.
        var bootstrapRejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(owner));
        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
            bootstrapRejected.Message);

        // #144: the isolated PostgreSQL17 CI database starts with a
        // default PUBLIC TEMPORARY privilege. Remove that privilege
        // explicitly for this separate-LOGIN runtime guard test only;
        // later restore the original PUBLIC grant in finally.
        var databaseName = new NpgsqlConnectionStringBuilder(
            owner.Database.GetDbConnection().ConnectionString).Database;
        var databaseIdentifier = "\"" + databaseName.Replace("\"", "\"\"") + "\"";
        var publicHadTemporary = await owner.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_database d
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) acl
                WHERE d.datname = CURRENT_DATABASE()
                  AND acl.grantee = 0::oid
                  AND acl.privilege_type = 'TEMPORARY'
            ) AS "Value"
            """).SingleAsync();

        var login = "f061_review_probe_" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var connection = new NpgsqlConnectionStringBuilder(
            owner.Database.GetDbConnection().ConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = false
        };

        // Both identifiers and the ephemeral password are constructed
        // exclusively from restricted alphanumeric random bytes. Never
        // echo connection strings or the generated secret to CI logs.
        await owner.Database.ExecuteSqlRawAsync(
            $"CREATE ROLE {login} LOGIN PASSWORD '{password}' " +
            "NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT " +
            "NOREPLICATION NOBYPASSRLS");
        try
        {
            await owner.Database.ExecuteSqlRawAsync(
                $"REVOKE TEMPORARY ON DATABASE {databaseIdentifier} FROM PUBLIC");
            // A bootstrap superuser can SET LOCAL ROLE to a restricted
            // current_user. Reject it because the original session_user
            // still holds privileged credentials.
            await using (var impersonation =
                await owner.Database.BeginTransactionAsync())
            {
                await owner.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE {login}");
                Assert.Equal(login,
                    await owner.Database.SqlQueryRaw<string>("""
                        SELECT CURRENT_USER AS "Value"
                        """).SingleAsync());
                var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(owner));
                Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                    rejected.Message);
                await impersonation.RollbackAsync();
            }
            await using (var caller = new NpgsqlConnection(connection.ConnectionString))
            {
                await caller.OpenAsync();
                async Task<object?> Scalar(string sql)
                {
                    await using var command = new NpgsqlCommand(sql, caller);
                    return await command.ExecuteScalarAsync();
                }
                async Task Denied(string sql)
                {
                    await using var command = new NpgsqlCommand(sql, caller);
                    var refused = await Assert.ThrowsAsync<PostgresException>(
                        async () => await command.ExecuteNonQueryAsync());
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                        refused.SqlState);
                }

                // A separately authenticated LOGIN (session_user) has
                // neither superuser nor PostgreSQL role-administration
                // rights. This cannot be faked with a client session GUC.
                Assert.Equal(login, Assert.IsType<string>(
                    await Scalar("SELECT session_user")));
                Assert.Equal(login, Assert.IsType<string>(
                    await Scalar("SELECT current_user")));
                Assert.False(Assert.IsType<bool>(await Scalar("""
                    SELECT rolsuper FROM pg_catalog.pg_roles
                    WHERE rolname = session_user
                    """)));
                Assert.False(Assert.IsType<bool>(await Scalar("""
                    SELECT pg_catalog.has_table_privilege(current_user,
                        'public.git_later_checkpoint_mirror_second_reviews',
                        'INSERT, UPDATE, DELETE')
                    """)));

                // The opt-in runtime-only guard checks the real role,
                // protected SQL owners/grants and all installed immutable
                // trigger identities. No login role switch or bootstrap.
                await using (var restrictedDb = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(connection.ConnectionString).Options))
                    await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                        restrictedDb);

                await Denied("""
                    INSERT INTO public.git_later_checkpoint_mirror_second_reviews
                    DEFAULT VALUES
                    """);
                await Denied("""
                    UPDATE public.git_later_checkpoint_mirror_second_reviews
                    SET evidence_sha256 = evidence_sha256 WHERE false
                    """);
                await Denied("""
                    DELETE FROM public.git_later_checkpoint_mirror_second_reviews
                    WHERE false
                    """);
                await Denied("""
                    ALTER TABLE public.git_later_checkpoint_mirror_second_reviews
                    DISABLE TRIGGER trg_f061v2e_second_review_closed
                    """);
                await Denied("SET ROLE pg_database_owner");

                // Custom PostgreSQL GUCs are caller-settable and MUST
                // NEVER authorize a SECURITY DEFINER writer. Neither a
                // forged flag nor a public search_path elevates privileges.
                await using (var guc = new NpgsqlCommand("""
                    SET "agic.f061_second_review_writer" = 'true'
                    """, caller))
                    await guc.ExecuteNonQueryAsync();
                await using (var searchPath = new NpgsqlCommand(
                    "SET search_path TO public", caller))
                    await searchPath.ExecuteNonQueryAsync();

                await Denied("""
                    INSERT INTO public.git_later_checkpoint_mirror_second_review_events
                    DEFAULT VALUES
                    """);
                await Denied("""
                    INSERT INTO public.git_later_checkpoint_mirror_second_review_session_consumptions
                    DEFAULT VALUES
                    """);
                await Denied("""
                    ALTER TABLE public.git_later_checkpoint_mirror_second_review_session_consumptions
                    DISABLE TRIGGER trg_f061v2e_second_review_session_closed
                    """);
            }
        }
        finally
        {
            try
            {
                // Only this disposable test LOGIN is dropped. No user
                // account, schema, security trigger or F-061 row is changed.
                await owner.Database.ExecuteSqlRawAsync(
                    $"DROP ROLE IF EXISTS {login}");
            }
            finally
            {
                if (publicHadTemporary)
                    await owner.Database.ExecuteSqlRawAsync(
                        $"GRANT TEMPORARY ON DATABASE {databaseIdentifier} TO PUBLIC");
            }
        }
    }
}
