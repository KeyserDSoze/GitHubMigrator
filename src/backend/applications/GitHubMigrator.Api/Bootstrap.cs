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
            CREATE TABLE IF NOT EXISTS migration_plan_drafts (
                snapshot_id uuid NOT NULL,
                destination_connection_id uuid NOT NULL,
                target_organization varchar(100) NOT NULL,
                revision integer NOT NULL,
                updated_at_utc timestamp with time zone NOT NULL,
                repositories_json jsonb NOT NULL,
                PRIMARY KEY (snapshot_id, destination_connection_id)
            );
            CREATE TABLE IF NOT EXISTS staging_assets (
                source_connection_id uuid NOT NULL,
                asset_type varchar(32) NOT NULL,
                source_id varchar(1024) NOT NULL,
                project_id varchar(512) NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                first_snapshot_id uuid NOT NULL,
                last_seen_snapshot_id uuid NOT NULL,
                last_changed_snapshot_id uuid NOT NULL,
                revision integer NOT NULL,
                missing boolean NOT NULL DEFAULT false,
                PRIMARY KEY (source_connection_id, asset_type, source_id)
            );
            CREATE INDEX IF NOT EXISTS ix_staging_assets_changed
                ON staging_assets(source_connection_id, last_changed_snapshot_id);
            CREATE INDEX IF NOT EXISTS ix_staging_assets_missing
                ON staging_assets(source_connection_id, missing);
            CREATE TABLE IF NOT EXISTS staging_snapshot_syncs (
                snapshot_id uuid PRIMARY KEY,
                source_connection_id uuid NOT NULL,
                source_collected_at_utc timestamp with time zone NOT NULL,
                applied_at_utc timestamp with time zone NOT NULL,
                observed integer NOT NULL,
                added integer NOT NULL,
                changed integer NOT NULL,
                unchanged integer NOT NULL,
                marked_missing integer NOT NULL,
                source_inventory_complete boolean NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_staging_snapshot_syncs_source
                ON staging_snapshot_syncs(source_connection_id, source_collected_at_utc);
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
