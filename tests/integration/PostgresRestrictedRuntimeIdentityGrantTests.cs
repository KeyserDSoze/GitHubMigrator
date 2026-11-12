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
