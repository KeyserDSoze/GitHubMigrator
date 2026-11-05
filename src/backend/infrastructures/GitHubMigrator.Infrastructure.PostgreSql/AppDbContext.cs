using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool MustChangePassword { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MigrationPlanRecord
{
    public Guid Id { get; set; }
    public Guid DiscoverySnapshotId { get; set; }
    public string TargetOrganization { get; set; } = "";
    public string PlanJson { get; set; } = "{}";
    public uint Version { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<MigrationPlanRecord> MigrationPlans => Set<MigrationPlanRecord>();
    public DbSet<ConnectionRecord> Connections => Set<ConnectionRecord>();
    public DbSet<EncryptedSecretRecord> ConnectionSecrets => Set<EncryptedSecretRecord>();
    public DbSet<DiscoveryInventoryRecord> Discoveries => Set<DiscoveryInventoryRecord>();
    public DbSet<IdentityMappingDraftRecord> IdentityMappingDrafts => Set<IdentityMappingDraftRecord>();
    public DbSet<MigrationPlanDraftRecord> MigrationPlanDrafts => Set<MigrationPlanDraftRecord>();
    public DbSet<StagingAssetRecord> StagingAssets => Set<StagingAssetRecord>();
    public DbSet<StagingSnapshotSyncRecord> StagingSnapshotSyncs => Set<StagingSnapshotSyncRecord>();
    public DbSet<StagingAssetChangeRecord> StagingAssetChanges => Set<StagingAssetChangeRecord>();
    public DbSet<IncrementalPreparationRecord> IncrementalPreparations =>
        Set<IncrementalPreparationRecord>();
    public DbSet<IncrementalPreparationItemRecord> IncrementalPreparationItems =>
        Set<IncrementalPreparationItemRecord>();
    public DbSet<IncrementalPreparationEventRecord> IncrementalPreparationEvents =>
        Set<IncrementalPreparationEventRecord>();
    public DbSet<IncrementalReviewRecord> IncrementalReviews =>
        Set<IncrementalReviewRecord>();
    public DbSet<IncrementalReviewEventRecord> IncrementalReviewEvents =>
        Set<IncrementalReviewEventRecord>();
    public DbSet<IncrementalTargetPreflightRecord> IncrementalTargetPreflights =>
        Set<IncrementalTargetPreflightRecord>();
    public DbSet<IncrementalTargetProbeRecord> IncrementalTargetProbes =>
        Set<IncrementalTargetProbeRecord>();
    public DbSet<IncrementalTargetPreflightEventRecord> IncrementalTargetPreflightEvents =>
        Set<IncrementalTargetPreflightEventRecord>();
    public DbSet<IncrementalOperationPlanRecord> IncrementalOperationPlans =>
        Set<IncrementalOperationPlanRecord>();
    public DbSet<IncrementalOperationStepRecord> IncrementalOperationSteps =>
        Set<IncrementalOperationStepRecord>();
    public DbSet<GitTransferWorkpackRecord> GitTransferWorkpacks =>
        Set<GitTransferWorkpackRecord>();
    public DbSet<GitTransferWorkpackEventRecord> GitTransferWorkpackEvents =>
        Set<GitTransferWorkpackEventRecord>();
    public DbSet<IncrementalGitRefReconciliationRecord> IncrementalGitRefReconciliations =>
        Set<IncrementalGitRefReconciliationRecord>();
    public DbSet<GitHubTargetRefEvidenceRecord> GitHubTargetRefEvidence =>
        Set<GitHubTargetRefEvidenceRecord>();
    public DbSet<IncrementalExecutionAuthorizationAttemptRecord> IncrementalExecutionAuthorizationAttempts =>
        Set<IncrementalExecutionAuthorizationAttemptRecord>();
    public DbSet<MigrationJobRecord> MigrationJobs => Set<MigrationJobRecord>();
    public DbSet<MigrationJobEventRecord> MigrationJobEvents => Set<MigrationJobEventRecord>();
    public DbSet<DiscoveryPageCheckpointRecord> DiscoveryPageCheckpoints => Set<DiscoveryPageCheckpointRecord>();
    public DbSet<DiscoveryRefObservationRecord> DiscoveryRefObservations => Set<DiscoveryRefObservationRecord>();
    public DbSet<DiscoveryIdentityObservationRecord> DiscoveryIdentityObservations => Set<DiscoveryIdentityObservationRecord>();
    public DbSet<DiscoveryGroupObservationRecord> DiscoveryGroupObservations => Set<DiscoveryGroupObservationRecord>();
    public DbSet<DiscoveryMembershipObservationRecord> DiscoveryMembershipObservations =>
        Set<DiscoveryMembershipObservationRecord>();
    public DbSet<DiscoveryAclObservationRecord> DiscoveryAclObservations =>
        Set<DiscoveryAclObservationRecord>();
    public DbSet<DiscoveryRefManifestRecord> DiscoveryRefManifests => Set<DiscoveryRefManifestRecord>();
    public DbSet<DiscoveryRefChunkRecord> DiscoveryRefChunks => Set<DiscoveryRefChunkRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<MigrationPlanRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TargetOrganization).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PlanJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsRowVersion();
        });
        builder.Entity<ConnectionRecord>(entity =>
        {
            entity.ToTable("migration_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(x => x.ProviderKey).HasColumnName("provider_key").HasMaxLength(80);
            entity.Property(x => x.Direction).HasColumnName("direction");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.SecretReference).HasColumnName("secret_reference").HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.ValidatedAtUtc).HasColumnName("validated_at_utc");
            entity.Property(x => x.ValidationCode).HasColumnName("validation_code").HasMaxLength(64);
            entity.Property(x => x.AuthenticatedAs).HasColumnName("authenticated_as").HasMaxLength(160);
            entity.Property(x => x.PermissionAssessment).HasColumnName("permission_assessment").HasMaxLength(64);
        });
        builder.Entity<DiscoveryInventoryRecord>(entity =>
        {
            entity.ToTable("migration_discoveries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ConnectionId).HasColumnName("connection_id");
            entity.Property(x => x.CollectedAtUtc).HasColumnName("collected_at_utc");
            entity.Property(x => x.Json).HasColumnName("inventory").HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ConnectionId, x.CollectedAtUtc });
        });
        builder.Entity<IdentityMappingDraftRecord>(entity =>
        {
            entity.ToTable("identity_mapping_drafts");
            entity.HasKey(x => new { x.SnapshotId, x.DestinationConnectionId });
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.TargetOrganization).HasColumnName("target_organization").HasMaxLength(100);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.MappingsJson).HasColumnName("mappings_json").HasColumnType("jsonb");
        });
        builder.Entity<MigrationPlanDraftRecord>(entity =>
        {
            entity.ToTable("migration_plan_drafts");
            entity.HasKey(x => new { x.SnapshotId, x.DestinationConnectionId });
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.TargetOrganization).HasColumnName("target_organization").HasMaxLength(100);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.RepositoriesJson).HasColumnName("repositories_json").HasColumnType("jsonb");
        });
        builder.Entity<StagingAssetRecord>(entity =>
        {
            entity.ToTable("staging_assets");
            entity.HasKey(x => new { x.SourceConnectionId, x.AssetType, x.SourceId });
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.AssetType).HasColumnName("asset_type").HasMaxLength(32);
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.ProjectId).HasColumnName("project_id").HasMaxLength(512);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.FirstSnapshotId).HasColumnName("first_snapshot_id");
            entity.Property(x => x.LastSeenSnapshotId).HasColumnName("last_seen_snapshot_id");
            entity.Property(x => x.LastChangedSnapshotId).HasColumnName("last_changed_snapshot_id");
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.Missing).HasColumnName("missing");
            entity.HasIndex(x => new { x.SourceConnectionId, x.LastChangedSnapshotId })
                .HasDatabaseName("ix_staging_assets_changed");
            entity.HasIndex(x => new { x.SourceConnectionId, x.Missing })
                .HasDatabaseName("ix_staging_assets_missing");
        });
        builder.Entity<StagingSnapshotSyncRecord>(entity =>
        {
            entity.ToTable("staging_snapshot_syncs");
            entity.HasKey(x => x.SnapshotId);
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.SourceCollectedAtUtc).HasColumnName("source_collected_at_utc");
            entity.Property(x => x.AppliedAtUtc).HasColumnName("applied_at_utc");
            entity.Property(x => x.Observed).HasColumnName("observed");
            entity.Property(x => x.Added).HasColumnName("added");
            entity.Property(x => x.Changed).HasColumnName("changed");
            entity.Property(x => x.Unchanged).HasColumnName("unchanged");
            entity.Property(x => x.MarkedMissing).HasColumnName("marked_missing");
            entity.Property(x => x.SourceInventoryComplete).HasColumnName("source_inventory_complete");
            entity.Property(x => x.JournalVersion).HasColumnName("journal_version").HasDefaultValue(0);
            entity.HasIndex(x => new { x.SourceConnectionId, x.SourceCollectedAtUtc })
                .HasDatabaseName("ix_staging_snapshot_syncs_source");
        });
        builder.Entity<StagingAssetChangeRecord>(entity =>
        {
            entity.ToTable("staging_asset_changes");
            entity.HasKey(x => new { x.SnapshotId, x.AssetType, x.SourceId });
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.AssetType).HasColumnName("asset_type").HasMaxLength(32);
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.Kind).HasColumnName("change_kind").HasMaxLength(12);
            entity.Property(x => x.PreviousFingerprint).HasColumnName("previous_fingerprint").HasMaxLength(64);
            entity.Property(x => x.CurrentFingerprint).HasColumnName("current_fingerprint").HasMaxLength(64);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.HasIndex(x => new { x.SourceConnectionId, x.SnapshotId, x.Kind })
                .HasDatabaseName("ix_staging_asset_changes_source_kind");
        });
        builder.Entity<IncrementalPreparationRecord>(entity =>
        {
            entity.ToTable("incremental_preparations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.EnqueuedBy).HasColumnName("enqueued_by");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(x => x.AvailableAtUtc).HasColumnName("available_at_utc");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.PreparedAtUtc).HasColumnName("prepared_at_utc");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(70);
            entity.Property(x => x.ItemCount).HasColumnName("item_count");
            entity.Property(x => x.SensitiveItemCount).HasColumnName("sensitive_item_count");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.HasIndex(x => new { x.SnapshotId, x.DestinationConnectionId })
                .IsUnique().HasDatabaseName("ux_incremental_preparations_pair");
            entity.HasIndex(x => new { x.State, x.AvailableAtUtc, x.CreatedAtUtc })
                .HasDatabaseName("ix_incremental_preparations_ready");
            entity.HasOne<StagingSnapshotSyncRecord>().WithMany()
                .HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<IncrementalPreparationItemRecord>(entity =>
        {
            entity.ToTable("incremental_preparation_items");
            entity.HasKey(x => new { x.PreparationId, x.AssetType, x.SourceId });
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.AssetType).HasColumnName("asset_type").HasMaxLength(32);
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.ChangeKind).HasColumnName("change_kind").HasMaxLength(12);
            entity.Property(x => x.PreviousFingerprint).HasColumnName("previous_fingerprint").HasMaxLength(64);
            entity.Property(x => x.CurrentFingerprint).HasColumnName("current_fingerprint").HasMaxLength(64);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.ReviewCategory).HasColumnName("review_category").HasMaxLength(20);
            entity.Property(x => x.ReviewState).HasColumnName("review_state").HasMaxLength(20);
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IncrementalPreparationEventRecord>(entity =>
        {
            entity.ToTable("incremental_preparation_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(30);
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(70);
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PreparationId, x.Id })
                .HasDatabaseName("ix_incremental_preparation_events_run");
        });
        builder.Entity<IncrementalReviewRecord>(entity =>
        {
            entity.ToTable("incremental_preparation_reviews");
            entity.HasKey(x => x.PreparationId);
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(28);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.DryRunSha256).HasColumnName("dry_run_sha256").HasMaxLength(64);
            entity.Property(x => x.DryRunAtUtc).HasColumnName("dry_run_at_utc");
            entity.Property(x => x.SourceValidatedAtUtc).HasColumnName("source_validated_at_utc");
            entity.Property(x => x.DestinationValidatedAtUtc).HasColumnName("destination_validated_at_utc");
            entity.Property(x => x.ItemCount).HasColumnName("item_count");
            entity.Property(x => x.SensitiveItemCount).HasColumnName("sensitive_item_count");
            entity.Property(x => x.RemovalItemCount).HasColumnName("removal_item_count");
            entity.Property(x => x.ReviewerId).HasColumnName("reviewer_id");
            entity.Property(x => x.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
            entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(48);
            entity.Property(x => x.SensitiveAcknowledged).HasColumnName("sensitive_acknowledged");
            entity.HasOne<IncrementalPreparationRecord>().WithOne()
                .HasForeignKey<IncrementalReviewRecord>(x => x.PreparationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IncrementalReviewEventRecord>(entity =>
        {
            entity.ToTable("incremental_preparation_review_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(28);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.DryRunSha256).HasColumnName("dry_run_sha256").HasMaxLength(64);
            entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(48);
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PreparationId, x.Id })
                .HasDatabaseName("ix_incremental_preparation_review_events");
        });
        builder.Entity<IncrementalTargetPreflightRecord>(entity =>
        {
            entity.ToTable("incremental_target_preflights");
            entity.HasKey(x => x.PreparationId);
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.ReviewSha256).HasColumnName("review_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.CheckedAtUtc).HasColumnName("checked_at_utc");
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(20);
            entity.Property(x => x.MembershipStatus).HasColumnName("membership_status").HasMaxLength(28);
            entity.Property(x => x.MembershipRole).HasColumnName("membership_role").HasMaxLength(20);
            entity.Property(x => x.PrivateRepositoryPolicy).HasColumnName("private_repository_policy").HasMaxLength(32);
            entity.Property(x => x.SourceValidatedAtUtc).HasColumnName("source_validated_at_utc");
            entity.Property(x => x.DestinationValidatedAtUtc).HasColumnName("destination_validated_at_utc");
            entity.Property(x => x.RepositoryCount).HasColumnName("repository_count");
            entity.Property(x => x.VisibleCollisions).HasColumnName("visible_collisions");
            entity.Property(x => x.UncertainRepositories).HasColumnName("uncertain_repositories");
            entity.HasOne<IncrementalPreparationRecord>().WithOne()
                .HasForeignKey<IncrementalTargetPreflightRecord>(x => x.PreparationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IncrementalTargetProbeRecord>(entity =>
        {
            entity.ToTable("incremental_target_preflight_probes");
            entity.HasKey(x => new { x.PreparationId, x.SourceRepositoryId });
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.HistoryStrategy).HasColumnName("history_strategy").HasMaxLength(40);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(28);
            entity.HasOne<IncrementalTargetPreflightRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IncrementalTargetPreflightEventRecord>(entity =>
        {
            entity.ToTable("incremental_target_preflight_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.CheckedAtUtc).HasColumnName("checked_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(20);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PreparationId, x.Id })
                .HasDatabaseName("ix_incremental_target_preflight_events");
        });
        builder.Entity<IncrementalOperationPlanRecord>(entity =>
        {
            entity.ToTable("incremental_operation_plans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.ReviewSha256).HasColumnName("review_sha256").HasMaxLength(64);
            entity.Property(x => x.PreparationSha256).HasColumnName("preparation_sha256").HasMaxLength(64);
            entity.Property(x => x.PreflightSha256).HasColumnName("preflight_sha256").HasMaxLength(64);
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by");
            entity.Property(x => x.RepositoryCount).HasColumnName("repository_count");
            entity.Property(x => x.StepCount).HasColumnName("step_count");
            entity.Property(x => x.ChangedRefCount).HasColumnName("changed_ref_count");
            entity.Property(x => x.SensitiveChangeCount).HasColumnName("sensitive_change_count");
            entity.HasIndex(x => new { x.PreparationId, x.PreflightSha256 })
                .IsUnique().HasDatabaseName("ux_incremental_operation_plans_proof");
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IncrementalOperationStepRecord>(entity =>
        {
            entity.ToTable("incremental_operation_plan_steps");
            entity.HasKey(x => new { x.PlanId, x.Ordinal });
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.Ordinal).HasColumnName("ordinal");
            entity.Property(x => x.DependsOnOrdinal).HasColumnName("depends_on_ordinal");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(20);
            entity.Property(x => x.BlockerCode).HasColumnName("blocker_code").HasMaxLength(64);
            entity.Property(x => x.ChangedRefs).HasColumnName("changed_refs");
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<GitTransferWorkpackRecord>(entity =>
        {
            entity.ToTable("git_transfer_workpacks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.CreatedBy).HasColumnName("created_by");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(24);
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(x => x.AvailableAtUtc).HasColumnName("available_at_utc");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.BlockedAtUtc).HasColumnName("blocked_at_utc");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(70);
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<IncrementalGitRefReconciliationRecord>().WithMany()
                .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.ReconciliationId).IsUnique()
                .HasDatabaseName("ux_git_transfer_workpack_reconciliation");
            entity.HasIndex(x => x.LeaseToken).IsUnique()
                .HasDatabaseName("ux_git_transfer_workpack_lease");
            entity.HasIndex(x => new { x.State, x.AvailableAtUtc, x.CreatedAtUtc })
                .HasDatabaseName("ix_git_transfer_workpack_claim");
            entity.HasIndex(x => new { x.PlanId, x.CreatedAtUtc })
                .HasDatabaseName("ix_git_transfer_workpack_plan");
        });
        builder.Entity<GitTransferWorkpackEventRecord>(entity =>
        {
            entity.ToTable("git_transfer_workpack_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(24);
            entity.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(24);
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(70);
            entity.HasOne<GitTransferWorkpackRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkpackId, x.Id })
                .HasDatabaseName("ix_git_transfer_workpack_events");
        });
        builder.Entity<IncrementalGitRefReconciliationRecord>(entity =>
        {
            entity.ToTable("incremental_git_ref_reconciliations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceManifestRootSha256).HasColumnName("source_manifest_root_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by");
            entity.Property(x => x.SourceRefCount).HasColumnName("source_ref_count");
            entity.Property(x => x.TargetRefCount).HasColumnName("target_ref_count");
            entity.Property(x => x.MatchingCount).HasColumnName("matching_count");
            entity.Property(x => x.CreateCandidateCount).HasColumnName("create_candidate_count");
            entity.Property(x => x.DivergedCount).HasColumnName("diverged_count");
            entity.Property(x => x.TargetOnlyCount).HasColumnName("target_only_count");
            entity.Property(x => x.ItemsJson).HasColumnName("items_json").HasColumnType("jsonb");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<GitHubTargetRefEvidenceRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new {
                x.PlanId, x.SourceRepositoryId, x.TargetObservationId,
                x.SourceEvidenceSha256
            }).IsUnique().HasDatabaseName("ux_git_ref_reconciliations_proof");
            entity.HasIndex(x => new { x.PlanId, x.SourceRepositoryId, x.CreatedAtUtc })
                .HasDatabaseName("ix_git_ref_reconciliations_latest");
        });
        builder.Entity<GitHubTargetRefEvidenceRecord>(entity =>
        {
            entity.ToTable("incremental_target_ref_evidence");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.DestinationValidatedAtUtc).HasColumnName("destination_validated_at_utc");
            entity.Property(x => x.RefCount).HasColumnName("ref_count");
            entity.Property(x => x.RefsJson).HasColumnName("refs_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PlanId, x.SourceRepositoryId, x.EvidenceSha256 })
                .IsUnique().HasDatabaseName("ux_target_ref_evidence_proof");
            entity.HasIndex(x => new { x.PlanId, x.SourceRepositoryId, x.ObservedAtUtc })
                .HasDatabaseName("ix_target_ref_evidence_latest");
        });
        builder.Entity<IncrementalExecutionAuthorizationAttemptRecord>(entity =>
        {
            entity.ToTable("incremental_execution_authorization_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.AtUtc).HasColumnName("at_utc");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.Decision).HasColumnName("decision").HasMaxLength(16);
            entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(64);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PlanId, x.Id })
                .HasDatabaseName("ix_incremental_execution_authorization_events");
        });
        builder.Entity<MigrationJobRecord>(entity =>
        {
            entity.ToTable("migration_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.EnqueuedBy).HasColumnName("enqueued_by");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(160);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(20);
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(x => x.AvailableAtUtc).HasColumnName("available_at_utc");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(70);
            entity.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            entity.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_migration_jobs_idempotency");
            entity.HasIndex(x => x.LeaseToken).IsUnique().HasDatabaseName("ux_migration_jobs_lease_token");
            entity.HasIndex(x => new { x.State, x.AvailableAtUtc, x.CreatedAtUtc })
                .HasDatabaseName("ix_migration_jobs_ready");
            entity.HasIndex(x => new { x.SourceConnectionId, x.CreatedAtUtc })
                .HasDatabaseName("ix_migration_jobs_source");
            entity.HasIndex(x => x.SourceConnectionId).IsUnique()
                .HasFilter("kind = 'DiscoverSource' AND state IN ('Queued', 'Running')")
                .HasDatabaseName("ux_migration_jobs_active_discovery");
        });
        builder.Entity<MigrationJobEventRecord>(entity =>
        {
            entity.ToTable("migration_job_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(32);
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(70);
            entity.HasIndex(x => new { x.JobId, x.Id }).HasDatabaseName("ix_migration_job_events_job");
        });
        builder.Entity<DiscoveryPageCheckpointRecord>(entity =>
        {
            entity.ToTable("discovery_page_checkpoints");
            entity.HasKey(x => new { x.JobId, x.ResourceKey, x.PageIndex });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
            entity.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            entity.Property(x => x.SafePayloadJson).HasColumnName("safe_payload_json").HasColumnType("jsonb");
            entity.Property(x => x.NextContinuation).HasColumnName("next_continuation").HasMaxLength(4096);
            entity.Property(x => x.SavedAtUtc).HasColumnName("saved_at_utc");
        });
        builder.Entity<DiscoveryRefObservationRecord>(entity =>
        {
            entity.ToTable("discovery_ref_observations");
            entity.HasKey(x => new { x.JobId, x.SourceId });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id").HasMaxLength(256);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
            entity.HasIndex(x => new { x.JobId, x.RepositoryId })
                .HasDatabaseName("ix_discovery_ref_observations_repo");
        });
        builder.Entity<DiscoveryGroupObservationRecord>(entity =>
        {
            entity.ToTable("discovery_group_observations");
            entity.HasKey(x => new { x.JobId, x.SourceId });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
        });
        builder.Entity<DiscoveryMembershipObservationRecord>(entity =>
        {
            entity.ToTable("discovery_membership_observations");
            entity.HasKey(x => new { x.JobId, x.SourceId });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.GroupDescriptor).HasColumnName("group_descriptor").HasMaxLength(1024);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
            entity.HasIndex(x => new { x.JobId, x.GroupDescriptor })
                .HasDatabaseName("ix_discovery_membership_observations_group");
        });
        builder.Entity<DiscoveryAclObservationRecord>(entity =>
        {
            entity.ToTable("discovery_acl_observations");
            entity.HasKey(x => new { x.JobId, x.SourceId });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.SecurityToken).HasColumnName("security_token").HasMaxLength(512);
            entity.Property(x => x.SecurityDescriptor).HasColumnName("security_descriptor").HasMaxLength(512);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
            entity.HasIndex(x => new { x.JobId, x.SecurityToken })
                .HasDatabaseName("ix_discovery_acl_observations_token");
            entity.HasOne<DiscoveryPageCheckpointRecord>().WithMany()
                .HasForeignKey(x => new { x.JobId, x.ResourceKey, x.PageIndex })
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DiscoveryIdentityObservationRecord>(entity =>
        {
            entity.ToTable("discovery_identity_observations");
            entity.HasKey(x => new { x.JobId, x.SourceId });
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceId).HasColumnName("source_id").HasMaxLength(1024);
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.ResourceKey).HasColumnName("resource_key").HasMaxLength(256);
            entity.Property(x => x.PageIndex).HasColumnName("page_index");
        });
        builder.Entity<DiscoveryRefManifestRecord>(entity =>
        {
            entity.ToTable("discovery_ref_manifests");
            entity.HasKey(x => x.JobId);
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.AssetCount).HasColumnName("asset_count");
            entity.Property(x => x.ChunkSize).HasColumnName("chunk_size");
            entity.Property(x => x.ChunkCount).HasColumnName("chunk_count");
            entity.Property(x => x.RootSha256).HasColumnName("root_sha256").HasMaxLength(64);
            entity.Property(x => x.PreparedAtUtc).HasColumnName("prepared_at_utc");
        });
        builder.Entity<DiscoveryRefChunkRecord>(entity =>
        {
            entity.ToTable("discovery_ref_chunks");
            entity.HasKey(x => new { x.JobId, x.Index });
            entity.HasOne<DiscoveryRefManifestRecord>().WithMany()
                .HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.Index).HasColumnName("chunk_index");
            entity.Property(x => x.AssetCount).HasColumnName("asset_count");
            entity.Property(x => x.FirstSourceId).HasColumnName("first_source_id").HasMaxLength(1024);
            entity.Property(x => x.LastSourceId).HasColumnName("last_source_id").HasMaxLength(1024);
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        });
        builder.Entity<EncryptedSecretRecord>(entity =>
        {
            entity.ToTable("migration_connection_secrets");
            entity.HasKey(x => x.Reference);
            entity.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(100);
            entity.Property(x => x.Ciphertext).HasColumnName("ciphertext");
            entity.Property(x => x.Nonce).HasColumnName("nonce");
            entity.Property(x => x.Tag).HasColumnName("tag");
        });
    }
}
