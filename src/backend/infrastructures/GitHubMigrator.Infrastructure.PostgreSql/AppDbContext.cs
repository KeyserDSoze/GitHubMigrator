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
    public DbSet<AiConnectionRecord> AiConnections => Set<AiConnectionRecord>();
    public DbSet<WorkflowProposalRecord> WorkflowProposals => Set<WorkflowProposalRecord>();
    public DbSet<WorkflowProposalEventRecord> WorkflowProposalEvents => Set<WorkflowProposalEventRecord>();
    public DbSet<WorkflowPublicationRecord> WorkflowPublications => Set<WorkflowPublicationRecord>();
    public DbSet<EncryptedSecretRecord> ConnectionSecrets => Set<EncryptedSecretRecord>();
    public DbSet<DiscoveryInventoryRecord> Discoveries => Set<DiscoveryInventoryRecord>();
    public DbSet<IdentityMappingDraftRecord> IdentityMappingDrafts => Set<IdentityMappingDraftRecord>();
    public DbSet<MigrationPlanDraftRecord> MigrationPlanDrafts => Set<MigrationPlanDraftRecord>();
    public DbSet<StagingAssetRecord> StagingAssets => Set<StagingAssetRecord>();
    public DbSet<GitIncrementalAncestryLedgerRecord> GitIncrementalAncestryLedgers
        => Set<GitIncrementalAncestryLedgerRecord>();
    public DbSet<GitIncrementalAncestryLedgerEventRecord> GitIncrementalAncestryLedgerEvents
        => Set<GitIncrementalAncestryLedgerEventRecord>();
    public DbSet<GitHubSourceSyncCheckpointRecord> GitHubSourceSyncCheckpoints
        => Set<GitHubSourceSyncCheckpointRecord>();
    public DbSet<GitHubSourceSyncCheckpointEventRecord> GitHubSourceSyncCheckpointEvents
        => Set<GitHubSourceSyncCheckpointEventRecord>();
    public DbSet<GitCheckpointTargetObservationRecord> GitCheckpointTargetObservations
        => Set<GitCheckpointTargetObservationRecord>();
    public DbSet<GitCheckpointTargetObservationEventRecord> GitCheckpointTargetObservationEvents
        => Set<GitCheckpointTargetObservationEventRecord>();
    public DbSet<GitCheckpointTargetHumanReviewRecord> GitCheckpointTargetHumanReviews =>
        Set<GitCheckpointTargetHumanReviewRecord>();
    public DbSet<GitCheckpointTargetHumanReviewEventRecord> GitCheckpointTargetHumanReviewEvents =>
        Set<GitCheckpointTargetHumanReviewEventRecord>();
    public DbSet<GitLaterCheckpointReconciliationRecord> GitLaterCheckpointReconciliations =>
        Set<GitLaterCheckpointReconciliationRecord>();
    public DbSet<GitLaterCheckpointWorkpackRecord> GitLaterCheckpointWorkpacks =>
        Set<GitLaterCheckpointWorkpackRecord>();
    public DbSet<GitLaterCheckpointBundleLeaseRecord> GitLaterCheckpointBundleLeases =>
        Set<GitLaterCheckpointBundleLeaseRecord>();
    public DbSet<GitLaterCheckpointBundleLeaseEventRecord> GitLaterCheckpointBundleLeaseEvents =>
        Set<GitLaterCheckpointBundleLeaseEventRecord>();
    public DbSet<GitLaterCheckpointBundleEvidenceRecord> GitLaterCheckpointBundleEvidence =>
        Set<GitLaterCheckpointBundleEvidenceRecord>();
    public DbSet<GitLaterCheckpointBundleEvidenceEventRecord> GitLaterCheckpointBundleEvents =>
        Set<GitLaterCheckpointBundleEvidenceEventRecord>();
    public DbSet<GitLaterCheckpointWorkpackEventRecord> GitLaterCheckpointWorkpackEvents =>
        Set<GitLaterCheckpointWorkpackEventRecord>();
    public DbSet<GitIncrementalCheckpointWorkpackRecord> GitIncrementalCheckpointWorkpacks =>
        Set<GitIncrementalCheckpointWorkpackRecord>();
    public DbSet<GitIncrementalCheckpointWorkpackEventRecord> GitIncrementalCheckpointWorkpackEvents =>
        Set<GitIncrementalCheckpointWorkpackEventRecord>();
    public DbSet<GitHubSourceCheckpointGitRefLedgerRecord> GitHubSourceCheckpointGitRefLedgers
        => Set<GitHubSourceCheckpointGitRefLedgerRecord>();
    public DbSet<GitHubSourceCheckpointGitRefLedgerEventRecord> GitHubSourceCheckpointGitRefLedgerEvents
        => Set<GitHubSourceCheckpointGitRefLedgerEventRecord>();
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
    public DbSet<AzureGitFetchJobRecord> AzureGitFetchJobs =>
        Set<AzureGitFetchJobRecord>();
    public DbSet<AzureGitFetchJobEventRecord> AzureGitFetchJobEvents =>
        Set<AzureGitFetchJobEventRecord>();
    public DbSet<GitSandboxDispatchRecord> GitSandboxDispatches =>
        Set<GitSandboxDispatchRecord>();
    public DbSet<GitSandboxDispatchEventRecord> GitSandboxDispatchEvents =>
        Set<GitSandboxDispatchEventRecord>();
    public DbSet<GitSourceMirrorLifecycleRecord> GitSourceMirrorLifecycles =>
        Set<GitSourceMirrorLifecycleRecord>();
    public DbSet<GitSourceMirrorLifecycleEventRecord> GitSourceMirrorLifecycleEvents =>
        Set<GitSourceMirrorLifecycleEventRecord>();
    public DbSet<GitHubRemoteObjectReconciliationRecord> GitHubRemoteObjectReconciliations =>
        Set<GitHubRemoteObjectReconciliationRecord>();
    public DbSet<GitHubRemoteObjectReconciliationEventRecord> GitHubRemoteObjectReconciliationEvents =>
        Set<GitHubRemoteObjectReconciliationEventRecord>();
    public DbSet<GitHubRemoteTransmitHazardRecord> GitHubRemoteTransmitHazards =>
        Set<GitHubRemoteTransmitHazardRecord>();
    public DbSet<GitHubRemoteTransmitHazardEventRecord> GitHubRemoteTransmitHazardEvents =>
        Set<GitHubRemoteTransmitHazardEventRecord>();
    public DbSet<GitHubRemoteTypedRecoveryBaselineRecord>
        GitHubRemoteTypedRecoveryBaselines =>
        Set<GitHubRemoteTypedRecoveryBaselineRecord>();
    public DbSet<GitHubRemoteTypedRecoveryBaselineEventRecord>
        GitHubRemoteTypedRecoveryBaselineEvents =>
        Set<GitHubRemoteTypedRecoveryBaselineEventRecord>();
    public DbSet<GitHubRemotePostSendClosureCheckpointRecord>
        GitHubRemotePostSendClosureCheckpoints =>
        Set<GitHubRemotePostSendClosureCheckpointRecord>();
    public DbSet<GitHubRemotePostSendClosureEventRecord>
        GitHubRemotePostSendClosureEvents =>
        Set<GitHubRemotePostSendClosureEventRecord>();
    public DbSet<GitHubRemoteSandboxPreSendBarrierRecord>
        GitHubRemoteSandboxPreSendBarriers =>
        Set<GitHubRemoteSandboxPreSendBarrierRecord>();
    public DbSet<GitHubRemoteSandboxPreSendBarrierEventRecord>
        GitHubRemoteSandboxPreSendBarrierEvents =>
        Set<GitHubRemoteSandboxPreSendBarrierEventRecord>();
    public DbSet<GitHubRemoteSandboxDispatchConfirmationRecord>
        GitHubRemoteSandboxDispatchConfirmations =>
        Set<GitHubRemoteSandboxDispatchConfirmationRecord>();
    public DbSet<GitHubRemoteSandboxDispatchConfirmationEventRecord>
        GitHubRemoteSandboxDispatchConfirmationEvents =>
        Set<GitHubRemoteSandboxDispatchConfirmationEventRecord>();
    public DbSet<GitHubRemoteSandboxDispatchIntentRecord>
        GitHubRemoteSandboxDispatchIntents =>
        Set<GitHubRemoteSandboxDispatchIntentRecord>();
    public DbSet<GitHubRemoteSandboxDispatchIntentEventRecord>
        GitHubRemoteSandboxDispatchIntentEvents =>
        Set<GitHubRemoteSandboxDispatchIntentEventRecord>();
    public DbSet<GitHubRemoteSandboxReviewSessionRecord>
        GitHubRemoteSandboxReviewSessions =>
        Set<GitHubRemoteSandboxReviewSessionRecord>();
    public DbSet<GitHubRemoteSandboxReviewSessionEventRecord>
        GitHubRemoteSandboxReviewSessionEvents =>
        Set<GitHubRemoteSandboxReviewSessionEventRecord>();
    public DbSet<GitHubRemoteAppPreflightEvidenceRecord>
        GitHubRemoteAppPreflightEvidence =>
        Set<GitHubRemoteAppPreflightEvidenceRecord>();
    public DbSet<GitHubRemoteAppPreflightEventRecord>
        GitHubRemoteAppPreflightEvents =>
        Set<GitHubRemoteAppPreflightEventRecord>();
    public DbSet<GitHubRemoteReadbackRecord> GitHubRemoteReadbacks =>
        Set<GitHubRemoteReadbackRecord>();
    public DbSet<GitHubRemoteReadbackEventRecord> GitHubRemoteReadbackEvents =>
        Set<GitHubRemoteReadbackEventRecord>();
    public DbSet<GitHubRemoteTestGrantRecord> GitHubRemoteTestGrants =>
        Set<GitHubRemoteTestGrantRecord>();
    public DbSet<GitHubRemoteTestGrantEventRecord> GitHubRemoteTestGrantEvents =>
        Set<GitHubRemoteTestGrantEventRecord>();
    public DbSet<GitHubRemoteSandboxFenceRecord> GitHubRemoteSandboxFences =>
        Set<GitHubRemoteSandboxFenceRecord>();
    public DbSet<GitHubRemoteSandboxFenceEventRecord> GitHubRemoteSandboxFenceEvents =>
        Set<GitHubRemoteSandboxFenceEventRecord>();
    public DbSet<GitHubTransferAttemptRecord> GitHubTransferAttempts =>
        Set<GitHubTransferAttemptRecord>();
    public DbSet<GitHubTransferAttemptEventRecord> GitHubTransferAttemptEvents =>
        Set<GitHubTransferAttemptEventRecord>();
    public DbSet<GitHubTargetPreviewLeaseRecord> GitHubTargetPreviewLeases =>
        Set<GitHubTargetPreviewLeaseRecord>();
    public DbSet<GitHubTargetPreviewLeaseEventRecord> GitHubTargetPreviewLeaseEvents =>
        Set<GitHubTargetPreviewLeaseEventRecord>();
    public DbSet<GitHubAppWriteScopeRecord> GitHubAppWriteScopes =>
        Set<GitHubAppWriteScopeRecord>();
    public DbSet<GitHubRemoteRefFenceRecord> GitHubRemoteRefFences =>
        Set<GitHubRemoteRefFenceRecord>();
    public DbSet<GitImportRehearsalRecord> GitImportRehearsals =>
        Set<GitImportRehearsalRecord>();
    public DbSet<GitHubImportIntentRecord> GitHubImportIntents => Set<GitHubImportIntentRecord>();
    public DbSet<GitHubImportIntentEventRecord> GitHubImportIntentEvents =>
        Set<GitHubImportIntentEventRecord>();
    public DbSet<GitHubImportReadinessRecord> GitHubImportReadiness =>
        Set<GitHubImportReadinessRecord>();
    public DbSet<GitSourceBundleEvidenceRecord> GitSourceBundleEvidence =>
        Set<GitSourceBundleEvidenceRecord>();
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
            entity.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(512);
        });
        builder.Entity<AiConnectionRecord>(entity =>
        {
            entity.ToTable("ai_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(32);
            entity.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(512);
            entity.Property(x => x.Model).HasColumnName("model").HasMaxLength(100);
            entity.Property(x => x.Auth).HasColumnName("auth").HasMaxLength(32);
            entity.Property(x => x.ApiVersion).HasColumnName("api_version").HasMaxLength(32);
            entity.Property(x => x.TokenParameter).HasColumnName("token_parameter").HasMaxLength(32);
            entity.Property(x => x.MaxOutputTokens).HasColumnName("max_output_tokens");
            entity.Property(x => x.Temperature).HasColumnName("temperature");
            entity.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
            entity.Property(x => x.MaxInputBytes).HasColumnName("max_input_bytes");
            entity.Property(x => x.SecretReference).HasColumnName("secret_reference").HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.VerifiedAtUtc).HasColumnName("verified_at_utc");
            entity.Property(x => x.VerificationCode).HasColumnName("verification_code").HasMaxLength(64);
            entity.Property(x => x.VerifiedModel).HasColumnName("verified_model").HasMaxLength(100);
            entity.Property(x => x.SendSourceContent).HasColumnName("send_source_content");
            entity.Property(x => x.ConsentAtUtc).HasColumnName("consent_at_utc");
            entity.Property(x => x.ConsentBy).HasColumnName("consent_by").HasMaxLength(160);
        });
        builder.Entity<WorkflowProposalRecord>(entity =>
        {
            entity.ToTable("workflow_proposals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id").HasMaxLength(100);
            entity.Property(x => x.PipelineId).HasColumnName("pipeline_id");
            entity.Property(x => x.AiConnectionId).HasColumnName("ai_connection_id");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(100);
            entity.Property(x => x.WorkflowYaml).HasColumnName("workflow_yaml");
            entity.Property(x => x.WorkflowSha256).HasColumnName("workflow_sha256").HasMaxLength(64);
            entity.Property(x => x.ModelNotes).HasColumnName("model_notes");
            entity.Property(x => x.Evidence).HasColumnName("evidence").HasColumnType("jsonb");
            entity.Property(x => x.CreatedById).HasColumnName("created_by_id");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(160);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.ApprovedSha256).HasColumnName("approved_sha256").HasMaxLength(64);
            entity.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).HasDefaultValue("Conversion");
            entity.Property(x => x.ParentProposalId).HasColumnName("parent_proposal_id");
            entity.HasIndex(x => new { x.SourceConnectionId, x.ProjectId, x.PipelineId, x.CreatedAtUtc })
                .HasDatabaseName("ix_workflow_proposals_pipeline");
        });
        builder.Entity<WorkflowProposalEventRecord>(entity =>
        {
            entity.ToTable("workflow_proposal_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ProposalId).HasColumnName("proposal_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(160);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.WorkflowSha256).HasColumnName("workflow_sha256").HasMaxLength(64);
            entity.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
            entity.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProposalId, x.Id }).HasDatabaseName("ix_workflow_proposal_events_proposal");
            entity.HasOne<WorkflowProposalRecord>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<WorkflowPublicationRecord>(entity =>
        {
            entity.ToTable("workflow_publications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProposalId).HasColumnName("proposal_id");
            entity.Property(x => x.PlanSha256).HasColumnName("plan_sha256").HasMaxLength(64);
            entity.Property(x => x.WorkflowSha256).HasColumnName("workflow_sha256").HasMaxLength(64);
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.BaseBranch).HasColumnName("base_branch").HasMaxLength(200);
            entity.Property(x => x.BaseSha).HasColumnName("base_sha").HasMaxLength(40);
            entity.Property(x => x.HeadBranch).HasColumnName("head_branch").HasMaxLength(120);
            entity.Property(x => x.WorkflowPath).HasColumnName("workflow_path").HasMaxLength(200);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(x => x.CommitSha).HasColumnName("commit_sha").HasMaxLength(40);
            entity.Property(x => x.PullNumber).HasColumnName("pull_number");
            entity.Property(x => x.PullUrl).HasColumnName("pull_url").HasMaxLength(512);
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(80);
            entity.Property(x => x.FailedAtState).HasColumnName("failed_at_state").HasMaxLength(24);
            entity.Property(x => x.WritesPossible).HasColumnName("writes_possible");
            entity.Property(x => x.RequestedById).HasColumnName("requested_by_id");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(160);
            entity.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(8).HasDefaultValue("Create");
            entity.Property(x => x.ReplacedBlobSha).HasColumnName("replaced_blob_sha").HasMaxLength(40);
            entity.Property(x => x.ReplacedWorkflowSha256).HasColumnName("replaced_workflow_sha256").HasMaxLength(64);
            entity.Property(x => x.ReleasedAtUtc).HasColumnName("released_at_utc");
            entity.Property(x => x.ReleasedById).HasColumnName("released_by_id");
            entity.Property(x => x.ReleasedBy).HasColumnName("released_by").HasMaxLength(160);
            entity.Property(x => x.ReleaseNote).HasColumnName("release_note").HasMaxLength(500);
            entity.Property(x => x.ReleaseEvidence).HasColumnName("release_evidence").HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProposalId, x.StartedAtUtc }).HasDatabaseName("ix_workflow_publications_proposal");
            entity.HasIndex(x => new { x.Organization, x.Repository, x.WorkflowPath }).HasDatabaseName("ix_workflow_publications_path");
            entity.HasOne<WorkflowProposalRecord>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
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
        // F-095 offline Git ancestry audit evidence and Recorded event.
        builder.Entity<GitIncrementalAncestryLedgerRecord>(entity =>
        {
            entity.ToTable("git_incremental_ancestry_ledgers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.OperationManifestSha256).HasColumnName("operation_manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceCheckpointAttestationSha256).HasColumnName("source_checkpoint_attestation_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceiptSha256).HasColumnName("receipt_sha256").HasMaxLength(64);
            entity.Property(x => x.ReceiptJson).HasColumnName("receipt_json");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubTargetRefEvidenceRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.PlanId, x.CheckpointId, x.TargetObservationId,
                x.SourceRepositoryId }).IsUnique()
                .HasDatabaseName("ux_f095_source_target_proof");
        });
        builder.Entity<GitIncrementalAncestryLedgerEventRecord>(entity =>
        {
            entity.ToTable("git_incremental_ancestry_ledger_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.LedgerId).HasColumnName("ledger_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.ReceiptSha256).HasColumnName("receipt_sha256").HasMaxLength(64);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitIncrementalAncestryLedgerRecord>().WithMany()
                .HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.LedgerId).IsUnique();
        });
        // F-090 source-only append-only checkpoint chain; DDL installed by
        // GitHubSourceSyncCheckpointSchema after EF's base tables.
        builder.Entity<GitHubSourceSyncCheckpointRecord>(entity =>
        {
            entity.ToTable("github_source_sync_checkpoints");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.PreviousCheckpointId).HasColumnName("previous_checkpoint_id");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ProofJson).HasColumnName("proof_json");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceVersion).HasColumnName("evidence_version").HasDefaultValue(1);
            entity.Property(x => x.RefChangeManifestJson).HasColumnName("ref_change_manifest_json");
            entity.Property(x => x.RefChangeAttestationSha256).HasColumnName("ref_change_attestation_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
        });
        builder.Entity<GitHubSourceSyncCheckpointEventRecord>(entity =>
        {
            entity.ToTable("github_source_sync_checkpoint_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        });
        // #109 later F-031 is entirely distinct from historical F-029
        // F-031 evidence. Append-only constraints/triggers are installed
        // by the separate startup schema initializer.
        builder.Entity<GitCheckpointTargetObservationRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_target_observations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name");
            entity.Property(x => x.Organization).HasColumnName("organization");
            entity.Property(x => x.ObjectFormat).HasColumnName("object_format");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.CandidateEvidenceSha256).HasColumnName("candidate_evidence_sha256");
            entity.Property(x => x.HumanReviewEvidenceSha256).HasColumnName("human_review_evidence_sha256");
            entity.Property(x => x.ReadPreflightEvidenceSha256).HasColumnName("read_preflight_evidence_sha256");
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.RefCount).HasColumnName("ref_count");
            entity.Property(x => x.ReceiptJson).HasColumnName("receipt_json");
            entity.Property(x => x.CanObserveTarget).HasColumnName("can_observe_target");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId })
                .IsUnique();
        });
        builder.Entity<GitCheckpointTargetObservationEventRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_target_observation_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ObservationId).HasColumnName("observation_id");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitCheckpointTargetObservationRecord>().WithMany()
                .HasForeignKey(x => x.ObservationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.ObservationId).IsUnique();
        });
        // F-097b SQL triggers and constraints are bootstrapped after F-090.
        builder.Entity<GitCheckpointTargetHumanReviewRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_target_human_reviews");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.CandidateEvidenceSha256).HasColumnName("candidate_evidence_sha256");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.Decision).HasColumnName("decision");
            entity.Property(x => x.ReceiptJson).HasColumnName("receipt_json");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
            entity.Property(x => x.CanObserveTarget).HasColumnName("can_observe_target");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId })
                .IsUnique();
        });
        builder.Entity<GitCheckpointTargetHumanReviewEventRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_target_human_review_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.DecisionId).HasColumnName("decision_id");
            entity.Property(x => x.ReviewerId).HasColumnName("reviewer_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitCheckpointTargetHumanReviewRecord>().WithMany()
                .HasForeignKey(x => x.DecisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.DecisionId).IsUnique();
        });
        builder.Entity<GitLaterCheckpointBundleLeaseRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_bundle_leases");
            entity.HasKey(x => x.WorkpackId);
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.BundlePlanSha256).HasColumnName("bundle_plan_sha256");
            entity.Property(x => x.WorkpackEvidenceSha256).HasColumnName("workpack_evidence_sha256");
            entity.Property(x => x.FullRefProofSha256).HasColumnName("full_ref_proof_sha256");
            entity.Property(x => x.TargetReceiptSha256).HasColumnName("target_receipt_sha256");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.State).HasColumnName("state");
            entity.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc");
            entity.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.DurableEvidencePersisted).HasColumnName("durable_evidence_persisted");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasOne<GitLaterCheckpointWorkpackRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StagingSnapshotSyncRecord>().WithMany()
                .HasForeignKey(x => x.SourceSnapshotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConnectionRecord>().WithMany()
                .HasForeignKey(x => x.SourceConnectionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitCheckpointTargetObservationRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.LeaseToken).IsUnique();
        });
        builder.Entity<GitLaterCheckpointBundleLeaseEventRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_bundle_lease_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.BundlePlanSha256).HasColumnName("bundle_plan_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitLaterCheckpointBundleLeaseRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.WorkpackId, x.Generation, x.Type }).IsUnique();
        });
        builder.Entity<GitLaterCheckpointBundleEvidenceRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_bundle_evidence");
            entity.HasKey(x => x.WorkpackId);
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.WorkpackEvidenceSha256).HasColumnName("workpack_evidence_sha256");
            entity.Property(x => x.BundlePlanSha256).HasColumnName("bundle_plan_sha256");
            entity.Property(x => x.FullRefProofSha256).HasColumnName("full_ref_proof_sha256");
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256");
            entity.Property(x => x.SourceManifestRootSha256).HasColumnName("source_manifest_root_sha256");
            entity.Property(x => x.TargetReceiptSha256).HasColumnName("target_receipt_sha256");
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256");
            entity.Property(x => x.BundleFileName).HasColumnName("bundle_file_name");
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256");
            entity.Property(x => x.BundleBytes).HasColumnName("bundle_bytes");
            entity.Property(x => x.RefCount).HasColumnName("ref_count");
            entity.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasOne<GitLaterCheckpointWorkpackRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StagingSnapshotSyncRecord>().WithMany()
                .HasForeignKey(x => x.SourceSnapshotId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitCheckpointTargetObservationRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.BundleFileName).IsUnique();
        });
        builder.Entity<GitLaterCheckpointBundleEvidenceEventRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_bundle_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitLaterCheckpointBundleEvidenceRecord>().WithOne()
                .HasForeignKey<GitLaterCheckpointBundleEvidenceEventRecord>(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitLaterCheckpointReconciliationRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_reconciliations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256");
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256");
            entity.Property(x => x.FullRefProofSha256).HasColumnName("full_ref_proof_sha256");
            entity.Property(x => x.TargetReceiptSha256).HasColumnName("target_receipt_sha256");
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256");
            entity.Property(x => x.ItemsJson).HasColumnName("items_json");
            entity.Property(x => x.SourceRefCount).HasColumnName("source_ref_count");
            entity.Property(x => x.TargetRefCount).HasColumnName("target_ref_count");
            entity.Property(x => x.MatchingCount).HasColumnName("matching_count");
            entity.Property(x => x.CreateCandidateCount).HasColumnName("create_candidate_count");
            entity.Property(x => x.DivergedCount).HasColumnName("diverged_count");
            entity.Property(x => x.TargetOnlyCount).HasColumnName("target_only_count");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StagingSnapshotSyncRecord>().WithMany()
                .HasForeignKey(x => x.SourceSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitCheckpointTargetObservationRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId,
                x.TargetObservationId }).IsUnique();
        });
        builder.Entity<GitLaterCheckpointWorkpackRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_workpacks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.TargetReceiptSha256).HasColumnName("target_receipt_sha256");
            entity.Property(x => x.FullRefProofSha256).HasColumnName("full_ref_proof_sha256");
            entity.Property(x => x.ReceiptJson).HasColumnName("receipt_json");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.State).HasColumnName("state");
            entity.Property(x => x.Phase).HasColumnName("phase");
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasOne<IncrementalPreparationRecord>().WithMany()
                .HasForeignKey(x => x.PreparationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalOperationPlanRecord>().WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StagingSnapshotSyncRecord>().WithMany()
                .HasForeignKey(x => x.SourceSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitLaterCheckpointReconciliationRecord>().WithOne()
                .HasForeignKey<GitLaterCheckpointWorkpackRecord>(x => x.ReconciliationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitCheckpointTargetObservationRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId,
                x.TargetObservationId }).IsUnique();
        });
        builder.Entity<GitLaterCheckpointWorkpackEventRecord>(entity =>
        {
            entity.ToTable("git_later_checkpoint_workpack_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitLaterCheckpointWorkpackRecord>().WithOne()
                .HasForeignKey<GitLaterCheckpointWorkpackEventRecord>(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitIncrementalCheckpointWorkpackRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_source_workpack_proofs");
            entity.HasKey(x => x.WorkpackId);
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.FullRefProofSha256).HasColumnName("full_ref_proof_sha256");
            entity.Property(x => x.ReceiptJson).HasColumnName("receipt_json");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId,
                x.TargetObservationId }).IsUnique();
            entity.HasIndex(x => x.ReconciliationId).IsUnique();
            entity.HasOne<GitTransferWorkpackRecord>().WithOne()
                .HasForeignKey<GitIncrementalCheckpointWorkpackRecord>(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitIncrementalCheckpointWorkpackEventRecord>(entity =>
        {
            entity.ToTable("git_checkpoint_source_workpack_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.Type).HasColumnName("type");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitIncrementalCheckpointWorkpackRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubSourceCheckpointGitRefLedgerRecord>(entity =>
        {
            entity.ToTable("github_checkpoint_source_ref_proofs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.SourceManifestRootSha256).HasColumnName("source_manifest_root_sha256").HasMaxLength(64);
            entity.Property(x => x.ProofJson).HasColumnName("proof_json");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.CanPush).HasColumnName("can_push");
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => new { x.CheckpointId, x.SourceRepositoryId }).IsUnique()
                .HasDatabaseName("ux_f097_checkpoint_source_repo");
            entity.HasOne<GitHubSourceSyncCheckpointRecord>().WithMany()
                .HasForeignKey(x => x.CheckpointId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubSourceCheckpointGitRefLedgerEventRecord>(entity =>
        {
            entity.ToTable("github_checkpoint_source_ref_proof_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.LedgerId).HasColumnName("ledger_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubSourceCheckpointGitRefLedgerRecord>().WithMany()
                .HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.LedgerId).IsUnique();
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
            entity.HasIndex(x => new { x.SourceConnectionId, x.State })
                .HasDatabaseName("ix_f066_incremental_health_source_state");
            entity.HasIndex(x => new { x.SourceConnectionId, x.PreparedAtUtc })
                .IsDescending(false, true)
                .HasDatabaseName("ix_f067_incremental_prepared_source_time")
                .HasFilter("state = 'Prepared'");
            entity.HasIndex(x => new { x.LeaseUntilUtc, x.Id })
                .HasDatabaseName("ix_f069_incremental_running_expiry")
                .HasFilter("state = 'Running'");
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
        builder.Entity<AzureGitFetchJobRecord>(entity =>
        {
            entity.ToTable("azure_git_fetch_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
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
            entity.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(70);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitTransferWorkpackRecord>().WithOne()
                .HasForeignKey<AzureGitFetchJobRecord>(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.WorkpackId).IsUnique()
                .HasDatabaseName("ux_azure_git_fetch_workpack");
            entity.HasIndex(x => x.LeaseToken).IsUnique()
                .HasDatabaseName("ux_azure_git_fetch_lease");
            entity.HasIndex(x => new { x.State, x.AvailableAtUtc, x.CreatedAtUtc })
                .HasDatabaseName("ix_azure_git_fetch_claim");
            entity.HasIndex(x => new { x.PlanId, x.CreatedAtUtc })
                .HasDatabaseName("ix_azure_git_fetch_plan");
        });
        builder.Entity<AzureGitFetchJobEventRecord>(entity =>
        {
            entity.ToTable("azure_git_fetch_job_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.JobId).HasColumnName("job_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(24);
            entity.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(24);
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(70);
            entity.HasOne<AzureGitFetchJobRecord>().WithMany()
                .HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.JobId, x.Id })
                .HasDatabaseName("ix_azure_git_fetch_events");
        });
        builder.Entity<GitSandboxDispatchRecord>(entity =>
        {
            entity.ToTable("git_sandbox_dispatches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.AttemptId).HasColumnName("attempt_id");
            entity.Property(x => x.SandboxId).HasColumnName("sandbox_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256")
                .HasMaxLength(64);
            entity.Property(x => x.PayloadSha256).HasColumnName("payload_sha256")
                .HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.ClaimToken).HasColumnName("claim_token");
            entity.Property(x => x.ClaimUntilUtc).HasColumnName("claim_until_utc");
            entity.Property(x => x.OutcomeSha256).HasColumnName("outcome_sha256")
                .HasMaxLength(64);
            entity.Property(x => x.OutcomeCode).HasColumnName("outcome_code")
                .HasMaxLength(40);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.RemoteWritesPerformed)
                .HasColumnName("remote_writes_performed");
            entity.Property(x => x.ExecutionAuthorized)
                .HasColumnName("execution_authorized");
            entity.Property(x => x.ReadyForExecution)
                .HasColumnName("ready_for_execution");
            entity.HasIndex(x => x.AttemptId).IsUnique()
                .HasDatabaseName("ux_git_sandbox_dispatch_attempt");
            entity.HasIndex(x => new { x.State, x.CreatedAtUtc, x.Id })
                .HasDatabaseName("ix_git_sandbox_dispatch_poll");
        });
        builder.Entity<GitSandboxDispatchEventRecord>(entity =>
        {
            entity.ToTable("git_sandbox_dispatch_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.DispatchId).HasColumnName("dispatch_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitSandboxDispatchRecord>().WithMany()
                .HasForeignKey(x => x.DispatchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.DispatchId, x.Id })
                .HasDatabaseName("ix_git_sandbox_dispatch_events");
        });
        builder.Entity<GitHubRemoteTestGrantRecord>(entity =>
        {
            entity.ToTable("github_remote_test_grants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ScopeId).HasColumnName("scope_id");
            entity.Property(x => x.AttemptId).HasColumnName("attempt_id");
            entity.Property(x => x.FenceRequestId).HasColumnName("fence_request_id");
            entity.Property(x => x.FenceGeneration).HasColumnName("fence_generation");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.OriginalPolicySha256).HasColumnName("original_policy_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineSha256).HasColumnName("baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.PreflightSha256).HasColumnName("preflight_sha256").HasMaxLength(64);
            entity.Property(x => x.CancellationTokenSha256).HasColumnName("cancellation_token_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.DeadlineUtc).HasColumnName("deadline_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasIndex(x => new { x.FenceRequestId, x.FenceGeneration })
                .HasDatabaseName("uq_remote_test_grant_fence_epoch").IsUnique();
            entity.HasOne<GitHubRemoteSandboxFenceRecord>().WithMany()
                .HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubTransferAttemptRecord>().WithMany()
                .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteTestGrantEventRecord>(entity =>
        {
            entity.ToTable("github_remote_test_grant_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteTestGrantRecord>().WithMany()
                .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.GrantId, x.Id })
                .HasDatabaseName("ix_remote_test_grant_events");
        });
        builder.Entity<GitSourceMirrorLifecycleRecord>(entity =>
        {
            entity.ToTable("git_source_mirror_lifecycles");
            entity.HasKey(x => x.MirrorId);
            entity.Property(x => x.MirrorId).HasColumnName("mirror_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RefsSha256).HasColumnName("refs_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.LeaseGeneration).HasColumnName("lease_generation");
            entity.Property(x => x.LeasedBy).HasColumnName("leased_by");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc");
            entity.Property(x => x.ObjectSetSha256).HasColumnName("object_set_sha256").HasMaxLength(64);
            entity.Property(x => x.ObjectCount).HasColumnName("object_count");
            entity.Property(x => x.MirrorProofSha256).HasColumnName("mirror_proof_sha256").HasMaxLength(64);
            entity.Property(x => x.VerifiedAtUtc).HasColumnName("verified_at_utc");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(70);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.RemotePushInvoked).HasColumnName("remote_push_invoked");
            entity.Property(x => x.RemoteWritesAuthorized).HasColumnName("remote_writes_authorized");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.WorkpackId).IsUnique()
                .HasDatabaseName("ux_f061_workpack");
        });
        builder.Entity<GitSourceMirrorLifecycleEventRecord>(entity =>
        {
            entity.ToTable("git_source_mirror_lifecycle_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.MirrorId).HasColumnName("mirror_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(24);
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.WorkerId).HasColumnName("worker_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitSourceMirrorLifecycleRecord>().WithMany()
                .HasForeignKey(x => x.MirrorId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.MirrorId, x.Id })
                .HasDatabaseName("ix_f061_event_history");
        });
        builder.Entity<GitHubRemoteObjectReconciliationRecord>(entity =>
        {
            entity.ToTable("github_remote_object_reconciliations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.ReadbackId).HasColumnName("readback_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(40);
            entity.Property(x => x.OriginalBaselineSha256).HasColumnName("original_baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetRefSetSha256).HasColumnName("target_ref_set_sha256").HasMaxLength(64);
            entity.Property(x => x.RemoteRefObservationSha256).HasColumnName("remote_ref_observation_sha256").HasMaxLength(64);
            entity.Property(x => x.FullObjectClosureSha256).HasColumnName("full_object_closure_sha256").HasMaxLength(64);
            entity.Property(x => x.RemoteMirrorReceiptSha256).HasColumnName("remote_mirror_receipt_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ObservedBy).HasColumnName("observed_by");
            entity.Property(x => x.VerifiedAtUtc).HasColumnName("verified_at_utc");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.RemotePushInvoked).HasColumnName("remote_push_invoked");
            entity.Property(x => x.RemoteWritesAuthorized).HasColumnName("remote_writes_authorized");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasOne<GitHubRemoteTestGrantRecord>().WithMany()
                .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubRemoteReadbackRecord>().WithMany()
                .HasForeignKey(x => x.ReadbackId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubRemoteTransmitHazardRecord>().WithMany()
                .HasForeignKey(x => x.HazardId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.GrantId).IsUnique()
                .HasDatabaseName("uq_f058_one_per_grant");
            entity.HasIndex(x => x.HazardId).IsUnique()
                .HasDatabaseName("uq_f058_one_per_hazard");
        });
        builder.Entity<GitHubRemoteObjectReconciliationEventRecord>(entity =>
        {
            entity.ToTable("github_remote_object_reconciliation_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteObjectReconciliationRecord>().WithMany()
                .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ReconciliationId, x.Id })
                .HasDatabaseName("ix_f058_events");
        });
        builder.Entity<GitHubRemoteTransmitHazardRecord>(entity =>
        {
            entity.ToTable("github_remote_transmit_hazards");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.FenceRequestId).HasColumnName("fence_request_id");
            entity.Property(x => x.FenceGeneration).HasColumnName("fence_generation");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineSha256).HasColumnName("baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.AbortTokenSha256).HasColumnName("abort_token_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.PreparedBy).HasColumnName("prepared_by");
            entity.Property(x => x.MarkedBy).HasColumnName("marked_by");
            entity.Property(x => x.DeadlineUtc).HasColumnName("deadline_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.TransportAuthorized).HasColumnName("transport_authorized");
            entity.Property(x => x.TransportInvoked).HasColumnName("transport_invoked");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.HasOne<GitHubRemoteTestGrantRecord>().WithMany()
                .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.GrantId).IsUnique()
                .HasDatabaseName("uq_f055_grant");
        });
        builder.Entity<GitHubRemoteTransmitHazardEventRecord>(entity =>
        {
            entity.ToTable("github_remote_transmit_hazard_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteTransmitHazardRecord>().WithMany()
                .HasForeignKey(x => x.HazardId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.HazardId, x.Id })
                .HasDatabaseName("ix_f055_hazard_events");
        });
        builder.Entity<GitHubRemoteSandboxReviewSessionRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_review_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.FenceRequestId).HasColumnName("fence_request_id");
            entity.Property(x => x.FenceGeneration).HasColumnName("fence_generation");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.ObservedBy).HasColumnName("observed_by");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.PreflightSha256).HasColumnName("preflight_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineSha256).HasColumnName("baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.DispatchReviewSha256).HasColumnName("dispatch_review_sha256").HasMaxLength(64);
            entity.Property(x => x.LiveRefEvidenceSha256).HasColumnName("live_ref_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.TransportInvoked).HasColumnName("transport_invoked");
            entity.Property(x => x.RemoteWritesAuthorized).HasColumnName("remote_writes_authorized");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.HazardId).IsUnique()
                .HasDatabaseName("github_remote_sandbox_review_sessions_hazard_id_key");
            entity.HasIndex(x => new { x.RepositoryId, x.RecordedAtUtc })
                .HasDatabaseName("ix_f078_session_repository_history");
        });
        builder.Entity<GitHubRemoteSandboxReviewSessionEventRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_review_session_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.SessionId).IsUnique()
                .HasDatabaseName("github_remote_sandbox_review_session_events_session_id_key");
            entity.HasOne<GitHubRemoteSandboxReviewSessionRecord>().WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteTypedRecoveryBaselineRecord>(entity =>
        {
            entity.ToTable("github_remote_typed_recovery_baselines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ConfirmationId).HasColumnName("confirmation_id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ScopeId).HasColumnName("scope_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(128);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(128);
            entity.Property(x => x.CapturedBy).HasColumnName("captured_by");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineSha256).HasColumnName("baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineObservedAtUtc).HasColumnName("baseline_observed_at_utc");
            entity.Property(x => x.BaselineEvidenceSha256).HasColumnName("baseline_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.BaselineRefsJson).HasColumnName("baseline_refs_json");
            entity.Property(x => x.CreateCandidatesJson).HasColumnName("create_candidates_json");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(40);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.CanSend).HasColumnName("can_send");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.ConfirmationId).IsUnique();
            entity.HasIndex(x => x.IntentId).IsUnique();
            entity.HasIndex(x => x.HazardId).IsUnique();
            entity.HasIndex(x => x.GrantId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxDispatchConfirmationRecord>()
                .WithMany().HasForeignKey(x => x.ConfirmationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteTypedRecoveryBaselineEventRecord>(entity =>
        {
            entity.ToTable("github_remote_typed_recovery_baseline_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.SnapshotId).IsUnique();
            entity.HasOne<GitHubRemoteTypedRecoveryBaselineRecord>()
                .WithMany().HasForeignKey(x => x.SnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemotePostSendClosureCheckpointRecord>(entity =>
        {
            entity.ToTable("github_remote_post_send_closure_checkpoints");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BarrierId).HasColumnName("barrier_id");
            entity.Property(x => x.ConfirmationId).HasColumnName("confirmation_id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.ReadbackId).HasColumnName("readback_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            entity.Property(x => x.BarrierEvidenceSha256).HasColumnName("barrier_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ReadbackEvidenceSha256).HasColumnName("readback_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationEvidenceSha256).HasColumnName("reconciliation_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.FullObjectClosureSha256).HasColumnName("full_object_closure_sha256").HasMaxLength(64);
            entity.Property(x => x.VerifiedAtUtc).HasColumnName("verified_at_utc");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(40);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.CanRetry).HasColumnName("can_retry");
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.BarrierId).IsUnique();
            entity.HasIndex(x => x.HazardId).IsUnique();
            entity.HasIndex(x => x.GrantId).IsUnique();
            entity.HasIndex(x => x.ReadbackId).IsUnique();
            entity.HasIndex(x => x.ReconciliationId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxPreSendBarrierRecord>().WithMany()
                .HasForeignKey(x => x.BarrierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubRemoteObjectReconciliationRecord>().WithMany()
                .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemotePostSendClosureEventRecord>(entity =>
        {
            entity.ToTable("github_remote_post_send_closure_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.CheckpointId).IsUnique();
            entity.HasOne<GitHubRemotePostSendClosureCheckpointRecord>()
                .WithMany().HasForeignKey(x => x.CheckpointId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxPreSendBarrierRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_pre_send_barriers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ConfirmationId).HasColumnName("confirmation_id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.MarkedBy).HasColumnName("marked_by");
            entity.Property(x => x.ConfirmationEvidenceSha256).HasColumnName("confirmation_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.IntentEvidenceSha256).HasColumnName("intent_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.SessionEvidenceSha256).HasColumnName("session_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.LiveRefEvidenceSha256).HasColumnName("live_ref_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(40);
            entity.Property(x => x.CanExecute).HasColumnName("can_execute");
            entity.Property(x => x.TransportInvoked).HasColumnName("transport_invoked");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.ConfirmationId).IsUnique();
            entity.HasIndex(x => x.IntentId).IsUnique();
            entity.HasIndex(x => x.SessionId).IsUnique();
            entity.HasIndex(x => x.HazardId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxDispatchConfirmationRecord>()
                .WithMany().HasForeignKey(x => x.ConfirmationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxPreSendBarrierEventRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_pre_send_barrier_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BarrierId).HasColumnName("barrier_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.BarrierId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxPreSendBarrierRecord>()
                .WithMany().HasForeignKey(x => x.BarrierId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxDispatchConfirmationRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_dispatch_confirmations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ConfirmedBy).HasColumnName("confirmed_by");
            entity.Property(x => x.IntentEvidenceSha256).HasColumnName("intent_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.SessionEvidenceSha256).HasColumnName("session_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.LiveRefEvidenceSha256).HasColumnName("live_ref_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.CanDispatch).HasColumnName("can_dispatch");
            entity.Property(x => x.TransportInvoked).HasColumnName("transport_invoked");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.IntentId).IsUnique();
            entity.HasIndex(x => x.SessionId).IsUnique();
            entity.HasIndex(x => x.HazardId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxDispatchIntentRecord>().WithMany()
                .HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxDispatchConfirmationEventRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_dispatch_confirmation_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ConfirmationId).HasColumnName("confirmation_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.ConfirmationId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxDispatchConfirmationRecord>()
                .WithMany().HasForeignKey(x => x.ConfirmationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxDispatchIntentRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_dispatch_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.HazardId).HasColumnName("hazard_id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.SessionEvidenceSha256).HasColumnName("session_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.DispatchReviewSha256).HasColumnName("dispatch_review_sha256").HasMaxLength(64);
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.LiveRefEvidenceSha256).HasColumnName("live_ref_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.CanExecute).HasColumnName("can_execute");
            entity.Property(x => x.TransportInvoked).HasColumnName("transport_invoked");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.Property(x => x.MigrationComplete).HasColumnName("migration_complete");
            entity.HasIndex(x => x.SessionId).IsUnique();
            entity.HasIndex(x => x.HazardId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxReviewSessionRecord>().WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxDispatchIntentEventRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_dispatch_intent_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(x => x.IntentId).IsUnique();
            entity.HasOne<GitHubRemoteSandboxDispatchIntentRecord>().WithMany()
                .HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteAppPreflightEvidenceRecord>(entity =>
        {
            entity.ToTable("github_remote_app_preflight_evidence");
            entity.HasKey(x => new { x.RequestId, x.Generation, x.EvidenceSha256 });
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.OriginalPolicySha256).HasColumnName("original_policy_sha256").HasMaxLength(64);
            entity.Property(x => x.CurrentPolicySha256).HasColumnName("current_policy_sha256").HasMaxLength(64);
            entity.Property(x => x.ExpectedBaselineSha256).HasColumnName("expected_baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.ObservedBaselineSha256).HasColumnName("observed_baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.MatchingBaselineRefs).HasColumnName("matching_baseline_refs");
            entity.Property(x => x.AbsentCreateCandidates).HasColumnName("absent_create_candidates");
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.PolicyObservedAtUtc).HasColumnName("policy_observed_at_utc");
            entity.Property(x => x.TokenExpiresAtUtc).HasColumnName("token_expires_at_utc");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(40);
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitHubRemoteSandboxFenceRecord>().WithMany()
                .HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.RepositoryId, x.RecordedAtUtc })
                .HasDatabaseName("ix_f077_preflight_repo_observed");
        });
        builder.Entity<GitHubRemoteAppPreflightEventRecord>(entity =>
        {
            entity.ToTable("github_remote_app_preflight_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteAppPreflightEvidenceRecord>().WithMany()
                .HasForeignKey(x => new { x.RequestId, x.Generation, x.EvidenceSha256 })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.RequestId, x.Generation, x.EvidenceSha256, x.Id })
                .HasDatabaseName("ix_f077_preflight_events");
        });
        builder.Entity<GitHubRemoteReadbackRecord>(entity =>
        {
            entity.ToTable("github_remote_readback_evidence");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.GrantId).HasColumnName("grant_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.BaselineSha256).HasColumnName("baseline_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.RefSetSha256).HasColumnName("ref_set_sha256").HasMaxLength(64);
            entity.Property(x => x.TipEvidenceSha256).HasColumnName("tip_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.AssessmentSha256).HasColumnName("assessment_sha256").HasMaxLength(64);
            entity.Property(x => x.JournalEvidenceSha256).HasColumnName("journal_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ObservedBy).HasColumnName("observed_by");
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(x => x.TransportAttempted).HasColumnName("transport_attempted");
            entity.Property(x => x.RemoteWritesAuthorized).HasColumnName("remote_writes_authorized");
            entity.Property(x => x.MayAutomaticallyRetry).HasColumnName("may_automatically_retry");
            entity.HasOne<GitHubRemoteTestGrantRecord>().WithMany()
                .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.GrantId)
                .HasDatabaseName("uq_f054_readback_grant").IsUnique();
        });
        builder.Entity<GitHubRemoteReadbackEventRecord>(entity =>
        {
            entity.ToTable("github_remote_readback_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ReadbackId).HasColumnName("readback_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteReadbackRecord>().WithMany()
                .HasForeignKey(x => x.ReadbackId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ReadbackId, x.Id })
                .HasDatabaseName("ix_f054_readback_events");
        });
        builder.Entity<GitHubRemoteSandboxFenceRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_fences");
            entity.HasKey(x => x.RepositoryId);
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ScopeId).HasColumnName("scope_id");
            entity.Property(x => x.AttemptId).HasColumnName("attempt_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.PolicySha256).HasColumnName("policy_sha256").HasMaxLength(64);
            entity.Property(x => x.TokenSha256).HasColumnName("token_sha256").HasMaxLength(64);
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.DeadlineUtc).HasColumnName("deadline_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasOne<GitHubTransferAttemptRecord>().WithMany()
                .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<GitHubRemoteSandboxFenceEventRecord>(entity =>
        {
            entity.ToTable("github_remote_sandbox_fence_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubRemoteSandboxFenceRecord>().WithMany()
                .HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.RepositoryId, x.Id })
                .HasDatabaseName("ix_remote_sandbox_fence_events");
        });
        builder.Entity<GitHubTransferAttemptRecord>(entity =>
        {
            entity.ToTable("github_transfer_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ScopeId).HasColumnName("scope_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.PolicySha256).HasColumnName("policy_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateSetSha256).HasColumnName("candidate_set_sha256").HasMaxLength(64);
            entity.Property(x => x.CandidateCount).HasColumnName("candidate_count");
            entity.Property(x => x.PolicyObservedAtUtc).HasColumnName("policy_observed_at_utc");
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.LeaseGeneration).HasColumnName("lease_generation");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
            entity.Property(x => x.BlockedAtUtc).HasColumnName("blocked_at_utc");
            entity.Property(x => x.ExpiredAtUtc).HasColumnName("expired_at_utc");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            entity.Property(x => x.TransportAttempted).HasColumnName("transport_attempted");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasIndex(x => x.ScopeId).IsUnique()
                .HasDatabaseName("ux_github_transfer_attempt_scope");
            entity.HasIndex(x => new { x.State, x.CreatedAtUtc, x.Id })
                .HasDatabaseName("ix_github_transfer_attempt_worker");
        });
        builder.Entity<GitHubTransferAttemptEventRecord>(entity =>
        {
            entity.ToTable("github_transfer_attempt_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.AttemptId).HasColumnName("attempt_id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(32);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubTransferAttemptRecord>().WithMany()
                .HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AttemptId, x.Id })
                .HasDatabaseName("ix_github_transfer_attempt_events");
        });
        builder.Entity<GitHubTargetPreviewLeaseRecord>(entity =>
        {
            entity.ToTable("github_target_preview_leases");
            entity.HasKey(x => x.RepositoryId);
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.LeaseToken).HasColumnName("lease_token");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(24);
            entity.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc");
            entity.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasIndex(x => x.LeaseToken).IsUnique()
                .HasDatabaseName("ux_github_target_preview_lease_token");
            entity.HasIndex(x => new { x.State, x.LeaseUntilUtc })
                .HasDatabaseName("ix_github_target_preview_lease_state");
        });
        builder.Entity<GitHubTargetPreviewLeaseEventRecord>(entity =>
        {
            entity.ToTable("github_target_preview_lease_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(24);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.PacketSha256).HasColumnName("packet_sha256").HasMaxLength(64);
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubTargetPreviewLeaseRecord>().WithMany()
                .HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.RepositoryId, x.Id })
                .HasDatabaseName("ix_github_target_preview_lease_events");
        });
        builder.Entity<GitHubAppWriteScopeRecord>(entity =>
        {
            entity.ToTable("github_app_write_scopes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.FenceId).HasColumnName("fence_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.RehearsalId).HasColumnName("rehearsal_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.Repository).HasColumnName("repository").HasMaxLength(100);
            entity.Property(x => x.InstallationId).HasColumnName("installation_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.IntentEvidenceSha256).HasColumnName("intent_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RehearsalEvidenceSha256).HasColumnName("rehearsal_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.FenceEvidenceSha256).HasColumnName("fence_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RemoteRefFingerprintSha256).HasColumnName("remote_ref_fingerprint_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.TokenExpiresAtUtc).HasColumnName("token_expires_at_utc");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.ScopedContentsWriteReported).HasColumnName("scoped_contents_write_reported");
            entity.Property(x => x.WriteCapabilityVerified).HasColumnName("write_capability_verified");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitHubRemoteRefFenceRecord>().WithOne()
                .HasForeignKey<GitHubAppWriteScopeRecord>(x => x.FenceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.FenceId).IsUnique()
                .HasDatabaseName("ux_github_app_write_scope_fence");
            entity.HasIndex(x => new { x.IntentId, x.ObservedAtUtc, x.Id })
                .HasDatabaseName("ix_github_app_write_scope_intent");
        });
        builder.Entity<GitHubRemoteRefFenceRecord>(entity =>
        {
            entity.ToTable("github_remote_ref_fences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.RehearsalId).HasColumnName("rehearsal_id");
            entity.Property(x => x.TargetObservationId).HasColumnName("target_observation_id");
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.DestinationValidatedAtUtc).HasColumnName("destination_validated_at_utc");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.IntentEvidenceSha256).HasColumnName("intent_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RehearsalEvidenceSha256).HasColumnName("rehearsal_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RemoteEvidenceSha256).HasColumnName("remote_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.RemoteRefFingerprintSha256).HasColumnName("remote_ref_fingerprint_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(24);
            entity.Property(x => x.BaselineCount).HasColumnName("baseline_count");
            entity.Property(x => x.RemoteCount).HasColumnName("remote_count");
            entity.Property(x => x.ChangedRefs).HasColumnName("changed_refs");
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.RemoteWritesPerformed).HasColumnName("remote_writes_performed");
            entity.Property(x => x.WriteCapabilityVerified).HasColumnName("write_capability_verified");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitImportRehearsalRecord>().WithMany()
                .HasForeignKey(x => x.RehearsalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GitHubTargetRefEvidenceRecord>().WithMany()
                .HasForeignKey(x => x.TargetObservationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.RehearsalId, x.RemoteEvidenceSha256 }).IsUnique()
                .HasDatabaseName("ux_github_remote_ref_fence_proof");
            entity.HasIndex(x => new { x.RehearsalId, x.ObservedAtUtc, x.Id })
                .HasDatabaseName("ix_github_remote_ref_fences_rehearsal");
        });
        builder.Entity<GitImportRehearsalRecord>(entity =>
        {
            entity.ToTable("git_import_rehearsals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.IntentEvidenceSha256).HasColumnName("intent_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.SafeRefPlanSha256).HasColumnName("safe_ref_plan_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.GitObjectFormat).HasColumnName("git_object_format").HasMaxLength(6);
            entity.Property(x => x.VerifiedSourceRefsSha256).HasColumnName("verified_source_refs_sha256").HasMaxLength(64);
            entity.Property(x => x.CreateCandidates).HasColumnName("create_candidates");
            entity.Property(x => x.AlreadyMatching).HasColumnName("already_matching");
            entity.Property(x => x.TargetOnlyNotMaterialized).HasColumnName("target_only_not_materialized");
            entity.Property(x => x.DestinationCreated).HasColumnName("destination_created");
            entity.Property(x => x.CreatedRefsSha256).HasColumnName("created_refs_sha256").HasMaxLength(64);
            entity.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.DestinationWritesPerformed).HasColumnName("destination_writes_performed");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitHubImportIntentRecord>().WithOne()
                .HasForeignKey<GitImportRehearsalRecord>(x => x.IntentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.IntentId).IsUnique()
                .HasDatabaseName("ux_git_import_rehearsals_intent");
            entity.HasIndex(x => new { x.WorkpackId, x.CompletedAtUtc, x.Id })
                .HasDatabaseName("ix_git_import_rehearsals_workpack");
        });
        builder.Entity<GitHubImportIntentRecord>(entity =>
        {
            entity.ToTable("github_import_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.ReadinessId).HasColumnName("readiness_id");
            entity.Property(x => x.ReadinessSha256).HasColumnName("readiness_sha256").HasMaxLength(64);
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.SafeRefPlanSha256).HasColumnName("safe_ref_plan_sha256").HasMaxLength(64);
            entity.Property(x => x.PolicySha256).HasColumnName("policy_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.CreateCandidates).HasColumnName("create_candidates");
            entity.Property(x => x.AlreadyMatching).HasColumnName("already_matching");
            entity.Property(x => x.DivergedBlocked).HasColumnName("diverged_blocked");
            entity.Property(x => x.TargetOnlyPreserved).HasColumnName("target_only_preserved");
            entity.Property(x => x.RulesetStatus).HasColumnName("ruleset_status").HasMaxLength(24);
            entity.Property(x => x.RulesetCount).HasColumnName("ruleset_count");
            entity.Property(x => x.ActiveRulesets).HasColumnName("active_rulesets");
            entity.Property(x => x.DefaultBranchProtection).HasColumnName("default_branch_protection").HasMaxLength(32);
            entity.Property(x => x.PolicyObservedAtUtc).HasColumnName("policy_observed_at_utc");
            entity.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(x => x.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
            entity.Property(x => x.WriteCapabilityVerified).HasColumnName("write_capability_verified");
            entity.Property(x => x.ExecutionAuthorized).HasColumnName("execution_authorized");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitHubImportReadinessRecord>().WithOne()
                .HasForeignKey<GitHubImportIntentRecord>(x => x.ReadinessId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.ReadinessId).IsUnique()
                .HasDatabaseName("ux_github_import_intent_readiness");
            entity.HasIndex(x => new { x.PlanId, x.CreatedAtUtc, x.Id })
                .HasDatabaseName("ix_github_import_intent_plan");
            entity.HasIndex(x => new { x.WorkpackId, x.CreatedAtUtc, x.Id })
                .HasDatabaseName("ix_github_import_intent_workpack");
        });
        builder.Entity<GitHubImportIntentEventRecord>(entity =>
        {
            entity.ToTable("github_import_intent_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.IntentId).HasColumnName("intent_id");
            entity.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20);
            entity.Property(x => x.ActorId).HasColumnName("actor_id");
            entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasOne<GitHubImportIntentRecord>().WithMany()
                .HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.IntentId, x.Id })
                .HasDatabaseName("ix_github_import_intent_events");
        });
        builder.Entity<GitHubImportReadinessRecord>(entity =>
        {
            entity.ToTable("github_import_readiness");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.PreparationId).HasColumnName("preparation_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.TargetRepositoryName).HasColumnName("target_repository_name").HasMaxLength(100);
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.DestinationConnectionId).HasColumnName("destination_connection_id");
            entity.Property(x => x.DestinationValidatedAtUtc).HasColumnName("destination_validated_at_utc");
            entity.Property(x => x.ManifestSha256).HasColumnName("manifest_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.TargetEvidenceSha256).HasColumnName("target_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.SourceBundleSha256).HasColumnName("source_bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.EvidenceSha256).HasColumnName("evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.RepositoryStatus).HasColumnName("repository_status").HasMaxLength(32);
            entity.Property(x => x.ReportedPush).HasColumnName("reported_push").HasMaxLength(24);
            entity.Property(x => x.Archived).HasColumnName("archived");
            entity.Property(x => x.Disabled).HasColumnName("disabled");
            entity.Property(x => x.DivergedRefs).HasColumnName("diverged_refs");
            entity.Property(x => x.TargetOnlyRefs).HasColumnName("target_only_refs");
            entity.Property(x => x.CreatedBy).HasColumnName("created_by");
            entity.Property(x => x.WriteCapabilityVerified).HasColumnName("write_capability_verified");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitTransferWorkpackRecord>().WithMany()
                .HasForeignKey(x => x.WorkpackId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncrementalGitRefReconciliationRecord>().WithMany()
                .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.WorkpackId, x.EvidenceSha256 }).IsUnique()
                .HasDatabaseName("ux_github_import_readiness_proof");
            entity.HasIndex(x => new { x.PlanId, x.ObservedAtUtc, x.Id })
                .HasDatabaseName("ix_github_import_readiness_plan");
            entity.HasIndex(x => new { x.WorkpackId, x.ObservedAtUtc, x.Id })
                .HasDatabaseName("ix_github_import_readiness_workpack");
        });
        builder.Entity<GitSourceBundleEvidenceRecord>(entity =>
        {
            entity.ToTable("git_source_bundle_evidence");
            entity.HasKey(x => x.WorkpackId);
            entity.Property(x => x.WorkpackId).HasColumnName("workpack_id");
            entity.Property(x => x.PlanId).HasColumnName("plan_id");
            entity.Property(x => x.ReconciliationId).HasColumnName("reconciliation_id");
            entity.Property(x => x.SourceRepositoryId).HasColumnName("source_repository_id").HasMaxLength(256);
            entity.Property(x => x.SourceEvidenceSha256).HasColumnName("source_evidence_sha256").HasMaxLength(64);
            entity.Property(x => x.ReconciliationSha256).HasColumnName("reconciliation_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleFileName).HasColumnName("bundle_file_name").HasMaxLength(40);
            entity.Property(x => x.BundleSha256).HasColumnName("bundle_sha256").HasMaxLength(64);
            entity.Property(x => x.BundleBytes).HasColumnName("bundle_bytes");
            entity.Property(x => x.RefCount).HasColumnName("ref_count");
            entity.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc");
            entity.Property(x => x.ReadyForExecution).HasColumnName("ready_for_execution");
            entity.HasOne<GitTransferWorkpackRecord>().WithOne()
                .HasForeignKey<GitSourceBundleEvidenceRecord>(x => x.WorkpackId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<IncrementalGitRefReconciliationRecord>().WithMany()
                .HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
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
