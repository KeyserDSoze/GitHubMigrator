using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

public static class Bootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, IHostEnvironment environment,
        ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Phase-0 bootstrap only: replace EnsureCreated with reviewed EF migrations before production.
        await db.Database.EnsureCreatedAsync();
        // Phase-0 additive schema bootstrap for databases created before F-002.
        // Reviewed EF migrations must replace EnsureCreated/DDL before production release.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS migration_connections (
                id uuid PRIMARY KEY,
                name varchar(120) NOT NULL,
                provider_key varchar(80) NOT NULL,
                direction integer NOT NULL,
                organization varchar(100) NOT NULL,
                secret_reference varchar(100) NOT NULL,
                created_at_utc timestamp with time zone NOT NULL,
                validated_at_utc timestamp with time zone NULL,
                validation_code varchar(64) NOT NULL,
                authenticated_as varchar(160) NULL,
                permission_assessment varchar(64) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS migration_discoveries (
                id uuid PRIMARY KEY,
                connection_id uuid NOT NULL,
                collected_at_utc timestamp with time zone NOT NULL,
                inventory jsonb NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_migration_discoveries_connection_collected
                ON migration_discoveries(connection_id, collected_at_utc);
            CREATE TABLE IF NOT EXISTS identity_mapping_drafts (
                snapshot_id uuid NOT NULL,
                destination_connection_id uuid NOT NULL,
                target_organization varchar(100) NOT NULL,
                revision integer NOT NULL,
                updated_at_utc timestamp with time zone NOT NULL,
                mappings_json jsonb NOT NULL,
                PRIMARY KEY (snapshot_id, destination_connection_id)
            );
            CREATE TABLE IF NOT EXISTS migration_connection_secrets (
                reference varchar(100) PRIMARY KEY,
                ciphertext bytea NOT NULL,
                nonce bytea NOT NULL,
                tag bytea NOT NULL
            );
            """);
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var roleName in new[] { "superadmin", "admin", "user" })
            if (!await roles.RoleExistsAsync(roleName))
                await roles.CreateAsync(new IdentityRole<Guid>(roleName));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var username = config["Bootstrap:Username"] ?? "superadmin";
        if (await users.FindByNameAsync(username) is not null) return;
        var password = config["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Bootstrap password must be provided in production.");
            password = "superadmin";
        }
        var user = new ApplicationUser { UserName = username, MustChangePassword = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException("Bootstrap user creation failed: " +
                string.Join("; ", result.Errors.Select(e => e.Code)));
        await users.AddToRoleAsync(user, "superadmin");
        logger.LogWarning("Bootstrap superadmin created; password change is required at first login UserId={UserId}", user.Id);
    }
}
