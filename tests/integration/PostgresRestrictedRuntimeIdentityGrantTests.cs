using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using GitHubMigrator.Infrastructure.PostgreSql;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        var discoveryId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            $"CREATE ROLE {login} LOGIN PASSWORD '{password}' " +
            "NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT " +
            "NOREPLICATION NOBYPASSRLS");
        try
        {
            // A known, immutable and credential-free source inventory
            // is committed only by the PRIVILEGED test bootstrap. The
            // independent runtime LOGIN below has no INSERT permission.
            var now = DateTimeOffset.UtcNow;
            await new PostgresDiscoveryRepository(db).SaveAsync(
                new DiscoveryInventory
                {
                    Id = discoveryId,
                    ConnectionId = harness.SourceConnectionId,
                    SourceProvider = "azure-devops-services",
                    Organization = "az-org",
                    CollectedAtUtc = now,
                    Snapshot = new DiscoverySnapshot
                    {
                        SnapshotId = discoveryId,
                        SourceSystem = SourceSystemKind.AzureDevOpsServices,
                        CollectedAt = now,
                        Projects = [], Identities = [], AccessGrants = []
                    },
                    Refs = [],
                    Issues = [],
                    Completeness = DiscoveryCompleteness.Partial,
                    IdentitiesComplete = false,
                    PermissionsComplete = false
                }, default);

            // psql's production wrapper uses --single-transaction plus
            // SET LOCAL; reproduce that atomic contract with the same SQL.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                // Execute the EXACT operator SQL through ADO.NET in
                // the SAME EF transaction. ExecuteSqlRawAsync applies
                // .NET composite formatting, so PostgreSQL regex bounds
                // like {2,62} would cause FormatException before the
                // SQL reaches PostgreSQL. Do not rewrite/escape the
                // source script: psql must execute identical bytes.
                await using (var command = db.Database.GetDbConnection()
                    .CreateCommand())
                {
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }

            // #133: table-level REVOKE SELECT does NOT remove a prior
            // GRANT SELECT(secret_reference). Validate that the exact
            // operator script fails its entire transaction rather than
            // accepting such a preexisting confidential-column grant.
            await db.Database.ExecuteSqlRawAsync(
                $"GRANT SELECT (secret_reference) ON " +
                $"public.migration_connections TO {login}");
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                await using var command =
                    db.Database.GetDbConnection().CreateCommand();
                command.Transaction = tx.GetDbTransaction();
                command.CommandText = sql;
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal("P0001", refused.SqlState);
                Assert.Contains("existing secret or write privileges",
                    refused.MessageText);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"REVOKE SELECT (secret_reference) ON " +
                    $"public.migration_connections FROM {login}");
            }

            // Even indirect access to the separately encrypted PAT store
            // makes this restricted pilot unsafe. Retry must still fail
            // rather than widen a grant or treat secrets as public.
            await db.Database.ExecuteSqlRawAsync(
                $"GRANT SELECT ON public.migration_connection_secrets TO {login}");
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                await using var command =
                    db.Database.GetDbConnection().CreateCommand();
                command.Transaction = tx.GetDbTransaction();
                command.CommandText = sql;
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal("P0001", refused.SqlState);
                Assert.Contains("existing secret or write privileges",
                    refused.MessageText);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"REVOKE SELECT ON public.migration_connection_secrets FROM {login}");
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
            // #133 incremental read-only capability; NO INSERT/UPDATE/
            // DELETE of connection records, credential-table access, or
            // provider calls for restricted runtime connections listing.
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public.migration_connections', 'SELECT')
                """));
            Assert.True(await Scalar<bool>("""
                SELECT pg_catalog.has_column_privilege(
                    CURRENT_USER, 'public.migration_connections', 'name', 'SELECT')
                """));
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_column_privilege(
                    CURRENT_USER, 'public.migration_connections', 'secret_reference', 'SELECT')
                """));
            // Direct SQL must be unable to retrieve the credential
            // reference even though the HTTP admin list remains usable.
            await using (var forbiddenColumn = new NpgsqlCommand(
                "SELECT secret_reference FROM public.migration_connections LIMIT 0",
                caller))
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(
                    () => forbiddenColumn.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public.migration_connections', 'INSERT,UPDATE,DELETE')
                """));
            Assert.True(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public.migration_discoveries', 'SELECT')
                """));
            Assert.False(await Scalar<bool>("""
                SELECT pg_catalog.has_table_privilege(
                    CURRENT_USER, 'public.migration_discoveries', 'INSERT,UPDATE,DELETE')
                """));
            await using (var forbiddenDiscoveryWrite = new NpgsqlCommand(
                "INSERT INTO public.migration_discoveries DEFAULT VALUES", caller))
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(
                    () => forbiddenDiscoveryWrite.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
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

            // #134: table DML checks alone do not catch TRUNCATE
            // (no DELETE row trigger), TRIGGER or writable sequences.
            // Exercise both the exact one-shot operator SQL and the
            // independent runtime startup guard against real PG17 grants.
            // Do not actually TRUNCATE any application/Identity data.
            var auditSequence = "f061_pilot_seq_" + Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE SEQUENCE public.{auditSequence}");
            try
            {
                foreach (var (privilege, kind, relation) in new[]
                {
                    ("TRUNCATE", "TABLE",
                        "public.git_later_checkpoint_mirror_second_reviews"),
                    ("TRUNCATE", "TABLE", "public.\"AspNetUsers\""),
                    ("TRIGGER", "TABLE", "public.migration_discoveries"),
                    // #136: privileges without DML still permit real
                    // maintenance, exclusive locks or new foreign keys.
                    // Probe a normal read-only table and the protected
                    // journal, without invoking any of those operations.
                    ("MAINTAIN", "TABLE", "public.migration_discoveries"),
                    ("REFERENCES", "TABLE", "public.migration_discoveries"),
                    ("MAINTAIN", "TABLE",
                        "public.git_later_checkpoint_mirror_second_reviews"),
                    ("REFERENCES", "TABLE",
                        "public.git_later_checkpoint_mirror_second_reviews"),
                    ("USAGE", "SEQUENCE", $"public.{auditSequence}"),
                    // #140: SELECT on its own allows currval even with
                    // no nextval/setval permission; this is NOT covered
                    // by the general table/column SELECT policy.
                    ("SELECT", "SEQUENCE", $"public.{auditSequence}"),
                    ("UPDATE", "SEQUENCE", $"public.{auditSequence}")
                })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT {privilege} ON {kind} {relation} TO {login}");
                    try
                    {
                        await using (var drifted = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options))
                        {
                            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                                () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                    drifted));
                            Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                refused.Message);
                        }

                        await using var tx = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                        await using var command = db.Database.GetDbConnection()
                            .CreateCommand();
                        command.Transaction = tx.GetDbTransaction();
                        command.CommandText = sql;
                        var rejected = await Assert.ThrowsAsync<PostgresException>(
                            () => command.ExecuteNonQueryAsync());
                        Assert.Equal("P0001", rejected.SqlState);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE {privilege} ON {kind} {relation} FROM {login}");
                    }
                }

                // #140: the same sequence SELECT permission may be
                // inherited through PUBLIC. The test never reads the
                // sequence or uses currval/nextval/setval; it grants
                // and revokes only a disposable catalog ACL.
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT SELECT ON SEQUENCE public.{auditSequence} TO PUBLIC");
                try
                {
                    await using (var drifted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options))
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                drifted));
                        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                            refused.Message);
                    }

                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                    await using var command = db.Database.GetDbConnection()
                        .CreateCommand();
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", rejected.SqlState);
                    Assert.Contains("unauthorized sequence privileges",
                        rejected.MessageText);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE SELECT ON SEQUENCE public.{auditSequence} FROM PUBLIC");
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP SEQUENCE IF EXISTS public.{auditSequence}");
            }

            // #135: a separate schema can be created by the bootstrap
            // operator AFTER the original Identity grants. Its CREATE
            // privilege is still DDL authority, even though neither the
            // database nor public schema grants CREATE. The guard and
            // exact deployment SQL must reject direct and PUBLIC grants.
            var driftSchema = "f061_runtime_schema_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE SCHEMA {driftSchema}");
            try
            {
                foreach (var grantee in new[] { login, "PUBLIC" })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT CREATE ON SCHEMA {driftSchema} TO {grantee}");
                    try
                    {
                        await using (var drifted = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options))
                        {
                            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                                () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                    drifted));
                            Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                refused.Message);
                        }

                        await using var tx = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                        await using var command = db.Database.GetDbConnection()
                            .CreateCommand();
                        command.Transaction = tx.GetDbTransaction();
                        command.CommandText = sql;
                        var rejected = await Assert.ThrowsAsync<PostgresException>(
                            () => command.ExecuteNonQueryAsync());
                        Assert.Equal("P0001", rejected.SqlState);
                        Assert.Contains("excessive inherited/DDL privileges",
                            rejected.MessageText);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE CREATE ON SCHEMA {driftSchema} FROM {grantee}");
                    }
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP SCHEMA IF EXISTS {driftSchema}");
            }


            // #136: PostgreSQL privilege evaluation includes ACLs
            // granted to PUBLIC. A harmless direct-login negative
            // probe alone does not prove PUBLIC grant drift is caught.
            // In particular, AspNetUsers UPDATE is allowlisted but its
            // REFERENCES permission must NEVER be allowlisted.
            foreach (var (privilege, relation) in new[]
            {
                ("MAINTAIN", "public.migration_discoveries"),
                ("REFERENCES", "public.\"AspNetUsers\"")
            })
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT {privilege} ON TABLE {relation} TO PUBLIC");
                try
                {
                    await using (var drifted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options))
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                drifted));
                        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                            refused.Message);
                    }

                    // The separately invoked operator SQL is a second
                    // independently implemented permission guard.
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                    await using var command = db.Database.GetDbConnection()
                        .CreateCommand();
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", rejected.SqlState);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE {privilege} ON TABLE {relation} FROM PUBLIC");
                }
            }

            // #136: PostgreSQL REFERENCES(column) grants do not
            // imply a table-level REFERENCES grant. Check direct
            // grants on immutable source metadata and PUBLIC grants
            // against the otherwise UPDATE-allowlisted AspNetUsers.
            // No foreign key is created by either negative probe.
            foreach (var (relation, column, grantee) in new[]
            {
                ("public.migration_discoveries", "id", login),
                ("public.\"AspNetUsers\"", "\"Id\"", "PUBLIC")
            })
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT REFERENCES ({column}) ON TABLE {relation} TO {grantee}");
                try
                {
                    await using (var drifted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options))
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                drifted));
                        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                            refused.Message);
                    }

                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                    await using var command = db.Database.GetDbConnection()
                        .CreateCommand();
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", rejected.SqlState);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE REFERENCES ({column}) ON TABLE {relation} FROM {grantee}");
                }
            }

            // #136: PostgreSQL17 materialized views use pg_class
            // relkind='m' and can be refreshed by MAINTAIN without
            // ordinary table DML. Verify drift on that relation kind,
            // using an intentionally unpopulated, disposable fixture.
            var auditMatview = "f061_pilot_mv_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE MATERIALIZED VIEW public.{auditMatview} " +
                "AS SELECT 1 AS marker WITH NO DATA");
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT MAINTAIN ON TABLE public.{auditMatview} TO {login}");
                try
                {
                    await using (var drifted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options))
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                drifted));
                        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                            refused.Message);
                    }

                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                    await using var command = db.Database.GetDbConnection()
                        .CreateCommand();
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", rejected.SqlState);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE MAINTAIN ON TABLE public.{auditMatview} FROM {login}");
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP MATERIALIZED VIEW IF EXISTS public.{auditMatview}");
            }

            // #137: a separately authenticated NOSUPERUSER login must
            // not inherit a privileged function's owner rights through
            // the default PUBLIC EXECUTE privilege, nor through a later
            // explicit EXECUTE grant. The disposable function is harmless
            // and is NEVER called; only catalog privileges are inspected.
            var definerName = "f061_pilot_definer_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION public.{definerName}() " +
                "RETURNS integer LANGUAGE sql SECURITY DEFINER " +
                "SET search_path = pg_catalog AS 'SELECT 1'");
            try
            {
                foreach (var grantee in new[] { "PUBLIC", login })
                {
                    // PostgreSQL17 grants EXECUTE to PUBLIC by default
                    // on a new function. Remove that default after the
                    // first negative probe; the second grant must prove
                    // a direct ACL is independently rejected.
                    if (grantee == login)
                        await db.Database.ExecuteSqlRawAsync(
                            $"GRANT EXECUTE ON FUNCTION public.{definerName}() TO {login}");
                    try
                    {
                        await using (var drifted = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options))
                        {
                            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                                () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                    drifted));
                            Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                refused.Message);
                        }

                        await using var tx = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                        await using var command = db.Database.GetDbConnection()
                            .CreateCommand();
                        command.Transaction = tx.GetDbTransaction();
                        command.CommandText = sql;
                        var rejected = await Assert.ThrowsAsync<PostgresException>(
                            () => command.ExecuteNonQueryAsync());
                        Assert.Equal("P0001", rejected.SqlState);
                        Assert.Contains("executable SECURITY DEFINER routine",
                            rejected.MessageText);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE EXECUTE ON FUNCTION public.{definerName}() FROM {grantee}");
                    }
                }

                // Removing both grants restores the original restricted
                // runtime startup gate; no function is executed.
                await using (var clean = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(connection.ConnectionString).Options))
                    await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(clean);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP FUNCTION IF EXISTS public.{definerName}()");
            }

            // #137: pg_proc also holds PROCEdures, not just functions.
            // A callable SECURITY DEFINER procedure in a second user
            // schema must be refused as well; USAGE on that schema
            // alone is legitimate and cannot be confused with CREATE.
            // The procedure is harmless and deliberately never CALLed.
            var routineSchema = "f061_definer_schema_" +
                Guid.NewGuid().ToString("N");
            var procedureName = "f061_definer_procedure_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE SCHEMA {routineSchema}");
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"CREATE PROCEDURE {routineSchema}.{procedureName}() " +
                    "LANGUAGE plpgsql SECURITY DEFINER " +
                    "SET search_path = pg_catalog AS 'BEGIN NULL; END;'");
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT USAGE ON SCHEMA {routineSchema} TO {login}");
                    try
                    {
                        foreach (var grantee in new[] { "PUBLIC", login })
                        {
                            // New PG17 procedures default to PUBLIC
                            // EXECUTE. Revoke it before testing the
                            // separately granted direct LOGIN privilege.
                            if (grantee == login)
                                await db.Database.ExecuteSqlRawAsync(
                                    $"GRANT EXECUTE ON PROCEDURE " +
                                    $"{routineSchema}.{procedureName}() TO {login}");
                            try
                            {
                                await using (var drifted = new AppDbContext(
                                    new DbContextOptionsBuilder<AppDbContext>()
                                        .UseNpgsql(connection.ConnectionString).Options))
                                {
                                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                                        () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                            drifted));
                                    Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                        refused.Message);
                                }

                                await using var tx =
                                    await db.Database.BeginTransactionAsync();
                                await db.Database.ExecuteSqlInterpolatedAsync(
                                    $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                                await using var command = db.Database.GetDbConnection()
                                    .CreateCommand();
                                command.Transaction = tx.GetDbTransaction();
                                command.CommandText = sql;
                                var rejected = await Assert.ThrowsAsync<PostgresException>(
                                    () => command.ExecuteNonQueryAsync());
                                Assert.Equal("P0001", rejected.SqlState);
                                Assert.Contains("executable SECURITY DEFINER routine",
                                    rejected.MessageText);
                            }
                            finally
                            {
                                await db.Database.ExecuteSqlRawAsync(
                                    $"REVOKE EXECUTE ON PROCEDURE " +
                                    $"{routineSchema}.{procedureName}() FROM {grantee}");
                            }
                        }

                        // USAGE alone does not confer CREATE or the
                        // routine's owner privileges; prove positive
                        // recovery before dropping the disposable schema.
                        await using var clean = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options);
                        await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(clean);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE USAGE ON SCHEMA {routineSchema} FROM {login}");
                    }
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"DROP PROCEDURE IF EXISTS {routineSchema}.{procedureName}()");
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP SCHEMA IF EXISTS {routineSchema}");
            }

            // #138: neither the runtime catalog guard nor provisioning
            // may silently treat a *new* connection metadata column as
            // approved just because it is not named secret_reference.
            // The separate restricted LOGIN receives only a temporary
            // column-scoped SELECT grant; no source data is queried.
            var extraMetadataColumn = "f061_unreviewed_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE public.migration_connections " +
                $"ADD COLUMN {extraMetadataColumn} text");
            try
            {
                foreach (var grantee in new[] { login, "PUBLIC" })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT SELECT ({extraMetadataColumn}) " +
                        $"ON TABLE public.migration_connections TO {grantee}");
                    try
                    {
                        await using (var drifted = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options))
                        {
                            var refused = await Assert.ThrowsAsync<
                                InvalidOperationException>(() =>
                                PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                    drifted));
                            Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                refused.Message);
                        }

                        await using var tx = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                        await using var command = db.Database.GetDbConnection()
                            .CreateCommand();
                        command.Transaction = tx.GetDbTransaction();
                        command.CommandText = sql;
                        var rejected = await Assert.ThrowsAsync<PostgresException>(
                            () => command.ExecuteNonQueryAsync());
                        Assert.Equal("P0001", rejected.SqlState);
                        Assert.Contains("unreviewed metadata column",
                            rejected.MessageText);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE SELECT ({extraMetadataColumn}) " +
                            $"ON TABLE public.migration_connections FROM {grantee}");
                    }
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE public.migration_connections " +
                    $"DROP COLUMN IF EXISTS {extraMetadataColumn}");
            }

            // #138: the read-only pilot also must reject SELECT on
            // unreviewed relations. SQL owner-privileged views can
            // reveal columns that the caller cannot read directly.
            // Check both table-wide and column-only ACL drift, then
            // effective PUBLIC SELECT on a harmless owner-created view.
            foreach (var privilege in new[]
            {
                "SELECT", "SELECT (repositories_json)"
            })
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT {privilege} ON TABLE " +
                    $"public.migration_plan_drafts TO {login}");
                try
                {
                    await using (var drifted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options))
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                drifted));
                        Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                            refused.Message);
                    }

                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                    await using var command = db.Database.GetDbConnection()
                        .CreateCommand();
                    command.Transaction = tx.GetDbTransaction();
                    command.CommandText = sql;
                    var rejected = await Assert.ThrowsAsync<PostgresException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", rejected.SqlState);
                    Assert.Contains("unreviewed SELECT privileges",
                        rejected.MessageText);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE {privilege} ON TABLE " +
                        $"public.migration_plan_drafts FROM {login}");
                }
            }

            var auditView = "f061_pilot_view_" +
                Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE VIEW public.{auditView} AS SELECT 1 AS marker");
            try
            {
                foreach (var grantee in new[] { "PUBLIC", login })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"GRANT SELECT ON TABLE public.{auditView} TO {grantee}");
                    try
                    {
                        await using (var drifted = new AppDbContext(
                            new DbContextOptionsBuilder<AppDbContext>()
                                .UseNpgsql(connection.ConnectionString).Options))
                        {
                            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                                () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                                    drifted));
                            Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                                refused.Message);
                        }

                        await using var tx = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_catalog.set_config('githubmigrator.runtime_role', {login}, true)");
                        await using var command = db.Database.GetDbConnection()
                            .CreateCommand();
                        command.Transaction = tx.GetDbTransaction();
                        command.CommandText = sql;
                        var rejected = await Assert.ThrowsAsync<PostgresException>(
                            () => command.ExecuteNonQueryAsync());
                        Assert.Equal("P0001", rejected.SqlState);
                        Assert.Contains("unreviewed SELECT privileges",
                            rejected.MessageText);
                    }
                    finally
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            $"REVOKE SELECT ON TABLE public.{auditView} FROM {grantee}");
                    }
                }
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP VIEW IF EXISTS public.{auditView}");
            }

            // Revoked grants restore the role before it starts the
            // real restricted ASP.NET HTTP host below.
            await using (var cleanRead = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(connection.ConnectionString).Options))
                await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(cleanRead);

            // The operator's successful initial grants are not a
            // permanent credential boundary. A later superuser grant
            // must cause the REAL runtime startup guard to refuse
            // the existing otherwise-safe, independently authenticated
            // login. Revoke only each disposable test grant in finally.
            await db.Database.ExecuteSqlRawAsync(
                $"GRANT SELECT (secret_reference) ON " +
                $"public.migration_connections TO {login}");
            try
            {
                await using var overGranted = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(connection.ConnectionString).Options);
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                        overGranted));
                Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                    refused.Message);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"REVOKE SELECT (secret_reference) ON " +
                    $"public.migration_connections FROM {login}");
            }

            await db.Database.ExecuteSqlRawAsync(
                $"GRANT SELECT ON public.migration_connection_secrets TO {login}");
            try
            {
                await using var overGranted = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(connection.ConnectionString).Options);
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                        overGranted));
                Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                    refused.Message);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"REVOKE SELECT ON public.migration_connection_secrets FROM {login}");
            }

            // Grant drift is not limited to secrets. The pilot runtime
            // is intentionally READ-ONLY on connection metadata and
            // immutable discovery snapshots. POST-bootsrap DML grants
            // must fail the real startup guard even before any SQL
            // mutation is attempted by an API route.
            foreach (var readOnlyTable in new[] {
                "migration_connections", "migration_discoveries"
            })
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT UPDATE ON public.{readOnlyTable} TO {login}");
                try
                {
                    await using var overGranted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options);
                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                            overGranted));
                    Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                        refused.Message);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE UPDATE ON public.{readOnlyTable} FROM {login}");
                }
            }

            // These other live API relations were never allowlisted
            // for DML in the restricted pilot. No data is modified:
            // each GRANT is held only for the negative startup test,
            // then revoked even if the assertion fails.
            foreach (var privilege in new[] {
                ("UPDATE", "UPDATE", "public.migration_plan_drafts"),
                ("UPDATE (repositories_json)", "UPDATE (repositories_json)",
                    "public.migration_plan_drafts"),
                ("DELETE", "DELETE", "public.\"AspNetUsers\"")
            })
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT {privilege.Item1} ON {privilege.Item3} TO {login}");
                try
                {
                    await using var overGranted = new AppDbContext(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseNpgsql(connection.ConnectionString).Options);
                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                            overGranted));
                    Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                        refused.Message);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"REVOKE {privilege.Item2} ON {privilege.Item3} FROM {login}");
                }
            }

            // A clean new connection must pass after each prohibited
            // grant is rolled back. Recovered privileges are not a
            // reason to skip the normal restricted HTTP login tests.
            await using (var restoredDb = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(connection.ConnectionString).Options))
                await PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(restoredDb);

            // REAL ASP.NET host with this separately authenticated PG17
            // runtime LOGIN. The production Program executes RuntimeOnly,
            // not Identity/DDL bootstrap. The existing superadmin account
            // was created by the earlier privileged harness bootstrap.
            // This assembly explicitly disables xUnit parallelization;
            // always restore the process env in finally.
            var priorDb = Environment.GetEnvironmentVariable(
                "ConnectionStrings__PostgreSql");
            var priorRuntime = Environment.GetEnvironmentVariable(
                "PostgreSql__RuntimeOnly");
            var priorBootstrap = Environment.GetEnvironmentVariable(
                "PostgreSql__BootstrapOnly");
            try
            {
                Environment.SetEnvironmentVariable(
                    "ConnectionStrings__PostgreSql", connection.ConnectionString);
                Environment.SetEnvironmentVariable("PostgreSql__RuntimeOnly", "true");
                Environment.SetEnvironmentVariable("PostgreSql__BootstrapOnly", "false");
                using var limitedHost = new WebApplicationFactory<Program>()
                    .WithWebHostBuilder(builder =>
                        builder.UseEnvironment("Development"));
                using var limitedClient = limitedHost.CreateClient();
                using (var health = await limitedClient.GetAsync("/api/health"))
                    Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using (var ready = await limitedClient.GetAsync("/api/ready"))
                    Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                using var auth = await limitedClient.PostAsJsonAsync(
                    "/api/auth/login",
                    new {
                        username = harness.SuperadminName,
                        password = ApiHarness.SuperadminPassword
                    });
                Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
                using var json = JsonDocument.Parse(
                    await auth.Content.ReadAsStringAsync());
                Assert.Equal(harness.SuperadminName,
                    json.RootElement.GetProperty("username").GetString());
                Assert.Equal("superadmin",
                    json.RootElement.GetProperty("role").GetString());
                var accessToken =
                    json.RootElement.GetProperty("accessToken").GetString();
                Assert.False(string.IsNullOrWhiteSpace(accessToken));

                // The full production policy checks the JWT and the
                // password-changed admin/superadmin role, then the real
                // PostgresConnectionRepository queries migration_connections
                // through the scoped restricted LOGIN (not bootstrap DB).
                using (var anonymousConnections =
                    await limitedClient.GetAsync("/api/connections"))
                    Assert.Equal(HttpStatusCode.Unauthorized,
                        anonymousConnections.StatusCode);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, "/api/connections");
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                using var listing = await limitedClient.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
                var content = await listing.Content.ReadAsStringAsync();
                using var result = JsonDocument.Parse(content);
                Assert.Equal(JsonValueKind.Array,
                    result.RootElement.ValueKind);
                var connectionIds = result.RootElement
                    .EnumerateArray()
                    .Select(item => item.GetProperty("id").GetGuid())
                    .ToHashSet();
                Assert.Contains(harness.SourceConnectionId, connectionIds);
                Assert.Contains(harness.DestinationConnectionId, connectionIds);
                Assert.DoesNotContain(ApiHarness.SourceToken, content);
                Assert.DoesNotContain(ApiHarness.TargetToken, content);
                Assert.DoesNotContain("secretReference", content,
                    StringComparison.OrdinalIgnoreCase);

                // The separately authenticated restricted host reads an
                // existing immutable source snapshot; it does NOT run
                // discovery/provider jobs or hold any source credential.
                using (var anonymousDiscoveries =
                    await limitedClient.GetAsync("/api/discoveries"))
                    Assert.Equal(HttpStatusCode.Unauthorized,
                        anonymousDiscoveries.StatusCode);
                using var discoveriesRequest = new HttpRequestMessage(
                    HttpMethod.Get, "/api/discoveries");
                discoveriesRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                using var discoveriesList =
                    await limitedClient.SendAsync(discoveriesRequest);
                Assert.Equal(HttpStatusCode.OK, discoveriesList.StatusCode);
                using (var summaries = JsonDocument.Parse(
                    await discoveriesList.Content.ReadAsStringAsync()))
                    Assert.Equal(JsonValueKind.Array,
                        summaries.RootElement.ValueKind);

                using var exactDiscoveryRequest = new HttpRequestMessage(
                    HttpMethod.Get, $"/api/discoveries/{discoveryId}");
                exactDiscoveryRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                using var exactDiscoveryResponse =
                    await limitedClient.SendAsync(exactDiscoveryRequest);
                Assert.Equal(HttpStatusCode.OK,
                    exactDiscoveryResponse.StatusCode);
                var exactDiscoveryBody =
                    await exactDiscoveryResponse.Content.ReadAsStringAsync();
                using (var exactDiscovery = JsonDocument.Parse(exactDiscoveryBody))
                {
                    Assert.Equal(discoveryId,
                        exactDiscovery.RootElement.GetProperty("id").GetGuid());
                    Assert.Equal(harness.SourceConnectionId,
                        exactDiscovery.RootElement.GetProperty("connectionId").GetGuid());
                }
                Assert.DoesNotContain(ApiHarness.SourceToken,
                    exactDiscoveryBody);
                Assert.DoesNotContain(ApiHarness.TargetToken,
                    exactDiscoveryBody);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "ConnectionStrings__PostgreSql", priorDb);
                Environment.SetEnvironmentVariable(
                    "PostgreSql__RuntimeOnly", priorRuntime);
                Environment.SetEnvironmentVariable(
                    "PostgreSql__BootstrapOnly", priorBootstrap);
            }

            // Even after receiving scoped Identity SELECT/UPDATE, role
            // membership (and hence SET ROLE) must fail the runtime
            // startup guard. The probe role grants no additional rights:
            // the forbidden authority is the ability to switch roles.
            var group = "f061_group_" + Guid.NewGuid().ToString("N");
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE ROLE {group} NOLOGIN");
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"GRANT {group} TO {login}");
                await using var impersonableDb = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseNpgsql(connection.ConnectionString).Options);
                var deniedMembership =
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync(
                            impersonableDb));
                Assert.Contains("PostgreSqlRuntimeRoleNotRestricted",
                    deniedMembership.Message);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"REVOKE {group} FROM {login}");
                await db.Database.ExecuteSqlRawAsync(
                    $"DROP ROLE IF EXISTS {group}");
            }

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
            // Only the test-owned immutable discovery row is deleted by
            // the separate privileged bootstrap context, never runtime.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM public.migration_discoveries WHERE id = {discoveryId}");
            // Remove test-only GRANT dependencies before deleting LOGIN.
            // No application tables or normal platform users are deleted.
            await db.Database.ExecuteSqlRawAsync($"DROP OWNED BY {login}");
            await db.Database.ExecuteSqlRawAsync($"DROP ROLE IF EXISTS {login}");
        }
    }
}
