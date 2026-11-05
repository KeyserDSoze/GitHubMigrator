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
                source_inventory_complete boolean NOT NULL,
                journal_version integer NOT NULL DEFAULT 0
            );
            -- F-025 upgrades an existing F-013 schema without pretending that
            -- older, already committed snapshots have historical change rows.
            ALTER TABLE staging_snapshot_syncs
                ADD COLUMN IF NOT EXISTS journal_version integer NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS ix_staging_snapshot_syncs_source
                ON staging_snapshot_syncs(source_connection_id, source_collected_at_utc);
            -- Immutable, snapshot-scoped delta events for future approved
            -- incremental synchronization; this table contains no secret or
            -- provider payload and cannot trigger target-side writes.
            CREATE TABLE IF NOT EXISTS staging_asset_changes (
                snapshot_id uuid NOT NULL,
                source_connection_id uuid NOT NULL,
                asset_type varchar(32) NOT NULL,
                source_id varchar(1024) NOT NULL,
                change_kind varchar(12) NOT NULL CHECK
                    (change_kind IN ('Added', 'Changed', 'Restored', 'Missing')),
                previous_fingerprint varchar(64) NULL,
                current_fingerprint varchar(64) NULL,
                revision integer NOT NULL CHECK (revision >= 1),
                PRIMARY KEY (snapshot_id, asset_type, source_id)
            );
            CREATE INDEX IF NOT EXISTS ix_staging_asset_changes_source_kind
                ON staging_asset_changes(source_connection_id, snapshot_id, change_kind);
            -- F-026 is a read-only, destination-bound preparation queue.
            -- No destination command, approval token, PAT or payload is stored.
            CREATE TABLE IF NOT EXISTS incremental_preparations (
                id uuid PRIMARY KEY,
                snapshot_id uuid NOT NULL REFERENCES staging_snapshot_syncs(snapshot_id),
                source_connection_id uuid NOT NULL,
                destination_connection_id uuid NOT NULL,
                enqueued_by uuid NOT NULL,
                state varchar(24) NOT NULL CHECK
                    (state IN ('Queued','Running','Prepared','Failed')),
                attempts integer NOT NULL DEFAULT 0,
                max_attempts integer NOT NULL DEFAULT 5,
                available_at_utc timestamp with time zone NOT NULL,
                lease_token uuid NULL,
                lease_until_utc timestamp with time zone NULL,
                created_at_utc timestamp with time zone NOT NULL,
                updated_at_utc timestamp with time zone NOT NULL,
                prepared_at_utc timestamp with time zone NULL,
                last_error_code varchar(70) NULL,
                item_count integer NOT NULL DEFAULT 0,
                sensitive_item_count integer NOT NULL DEFAULT 0,
                evidence_sha256 varchar(64) NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_incremental_preparations_pair
                ON incremental_preparations(snapshot_id, destination_connection_id);
            CREATE INDEX IF NOT EXISTS ix_incremental_preparations_ready
                ON incremental_preparations(state, available_at_utc, created_at_utc);
            CREATE TABLE IF NOT EXISTS incremental_preparation_items (
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id)
                    ON DELETE CASCADE,
                asset_type varchar(32) NOT NULL,
                source_id varchar(1024) NOT NULL,
                change_kind varchar(12) NOT NULL CHECK
                    (change_kind IN ('Added','Changed','Restored','Missing')),
                previous_fingerprint varchar(64) NULL,
                current_fingerprint varchar(64) NULL,
                revision integer NOT NULL CHECK (revision >= 1),
                review_category varchar(20) NOT NULL,
                review_state varchar(20) NOT NULL CHECK (review_state = 'NeedsReview'),
                PRIMARY KEY (preparation_id, asset_type, source_id)
            );
            CREATE TABLE IF NOT EXISTS incremental_preparation_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id)
                    ON DELETE CASCADE,
                event_type varchar(30) NOT NULL,
                attempt integer NOT NULL,
                occurred_at_utc timestamp with time zone NOT NULL,
                error_code varchar(70) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_incremental_preparation_events_run
                ON incremental_preparation_events(preparation_id, id);
            CREATE TABLE IF NOT EXISTS migration_jobs (
                id uuid PRIMARY KEY,
                kind varchar(40) NOT NULL,
                snapshot_id uuid NOT NULL,
                source_connection_id uuid NOT NULL,
                enqueued_by uuid NOT NULL,
                idempotency_key varchar(160) NOT NULL,
                state varchar(20) NOT NULL,
                attempts integer NOT NULL DEFAULT 0,
                max_attempts integer NOT NULL DEFAULT 5,
                available_at_utc timestamp with time zone NOT NULL,
                lease_token uuid NULL,
                lease_until_utc timestamp with time zone NULL,
                created_at_utc timestamp with time zone NOT NULL,
                updated_at_utc timestamp with time zone NOT NULL,
                completed_at_utc timestamp with time zone NULL,
                last_error_code varchar(70) NULL,
                result_json jsonb NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_migration_jobs_idempotency
                ON migration_jobs(idempotency_key);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_migration_jobs_lease_token
                ON migration_jobs(lease_token);
            CREATE INDEX IF NOT EXISTS ix_migration_jobs_ready
                ON migration_jobs(state, available_at_utc, created_at_utc);
            CREATE INDEX IF NOT EXISTS ix_migration_jobs_source
                ON migration_jobs(source_connection_id, created_at_utc);
            -- Only one queued or running discovery per Azure DevOps source.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_migration_jobs_active_discovery
                ON migration_jobs(source_connection_id)
                WHERE kind = 'DiscoverSource' AND state IN ('Queued', 'Running');
            CREATE TABLE IF NOT EXISTS migration_job_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                event_type varchar(32) NOT NULL,
                attempt integer NOT NULL,
                occurred_at_utc timestamp with time zone NOT NULL,
                error_code varchar(70) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_migration_job_events_job
                ON migration_job_events(job_id, id);
            CREATE TABLE IF NOT EXISTS discovery_page_checkpoints (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                request_hash varchar(64) NOT NULL,
                safe_payload_json jsonb NOT NULL,
                next_continuation varchar(4096) NULL,
                saved_at_utc timestamp with time zone NOT NULL,
                PRIMARY KEY (job_id, resource_key, page_index)
            );
            -- Provisional Git ref observations: atomic with committed source pages.
            -- These are NOT the committed canonical catalog and cannot mark missing refs.
            CREATE TABLE IF NOT EXISTS discovery_ref_observations (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_id varchar(1024) NOT NULL,
                repository_id varchar(256) NOT NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                PRIMARY KEY (job_id, source_id),
                FOREIGN KEY (job_id, resource_key, page_index)
                    REFERENCES discovery_page_checkpoints(job_id, resource_key, page_index)
                    ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_discovery_ref_observations_repo
                ON discovery_ref_observations(job_id, repository_id);
            CREATE TABLE IF NOT EXISTS discovery_group_observations (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_id varchar(1024) NOT NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                PRIMARY KEY (job_id, source_id),
                FOREIGN KEY (job_id, resource_key, page_index)
                    REFERENCES discovery_page_checkpoints(job_id, resource_key, page_index)
                    ON DELETE CASCADE
            );
            -- Direct Graph membership observations, checkpoint-provenanced.
            -- This is raw evidence, not effective GitHub/Azure access.
            CREATE TABLE IF NOT EXISTS discovery_membership_observations (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_id varchar(1024) NOT NULL,
                group_descriptor varchar(1024) NOT NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                PRIMARY KEY (job_id, source_id),
                FOREIGN KEY (job_id, resource_key, page_index)
                    REFERENCES discovery_page_checkpoints(job_id, resource_key, page_index)
                    ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_discovery_membership_observations_group
                ON discovery_membership_observations(job_id, group_descriptor);
            -- Sanitized Azure DevOps raw ACL evidence. These masks never imply
            -- effective rights or destination GitHub grants.
            CREATE TABLE IF NOT EXISTS discovery_acl_observations (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_id varchar(1024) NOT NULL,
                security_token varchar(512) NOT NULL,
                security_descriptor varchar(512) NOT NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                PRIMARY KEY (job_id, source_id),
                FOREIGN KEY (job_id, resource_key, page_index)
                    REFERENCES discovery_page_checkpoints(job_id, resource_key, page_index)
                    ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_discovery_acl_observations_token
                ON discovery_acl_observations(job_id, security_token);
            CREATE TABLE IF NOT EXISTS discovery_identity_observations (
                job_id uuid NOT NULL REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_id varchar(1024) NOT NULL,
                fingerprint varchar(64) NOT NULL,
                payload_json jsonb NOT NULL,
                resource_key varchar(256) NOT NULL,
                page_index integer NOT NULL,
                PRIMARY KEY (job_id, source_id),
                FOREIGN KEY (job_id, resource_key, page_index)
                    REFERENCES discovery_page_checkpoints(job_id, resource_key, page_index)
                    ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS discovery_ref_manifests (
                job_id uuid PRIMARY KEY REFERENCES migration_jobs(id) ON DELETE CASCADE,
                source_connection_id uuid NOT NULL,
                asset_count integer NOT NULL CHECK (asset_count >= 0),
                chunk_size integer NOT NULL CHECK (chunk_size > 0),
                chunk_count integer NOT NULL CHECK (chunk_count >= 0),
                root_sha256 varchar(64) NOT NULL,
                prepared_at_utc timestamp with time zone NOT NULL
            );
            CREATE TABLE IF NOT EXISTS discovery_ref_chunks (
                job_id uuid NOT NULL REFERENCES discovery_ref_manifests(job_id) ON DELETE CASCADE,
                chunk_index integer NOT NULL CHECK (chunk_index >= 0),
                asset_count integer NOT NULL CHECK (asset_count > 0 AND asset_count <= 250),
                first_source_id varchar(1024) NOT NULL,
                last_source_id varchar(1024) NOT NULL,
                sha256 varchar(64) NOT NULL,
                PRIMARY KEY (job_id, chunk_index)
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
