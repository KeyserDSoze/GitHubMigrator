using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitHub;
using GitHubMigrator.Infrastructure.KeyVault;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureStorage;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required.");
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 UTF-8 bytes.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = false;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = !builder.Environment.IsDevelopment();
    options.Password.RequireUppercase = !builder.Environment.IsDevelopment();
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = !builder.Environment.IsDevelopment();
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();

// Only traceability statuses use string enums (camelCase) in the public REST contract.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<TraceabilityStatus>(JsonNamingPolicy.CamelCase)));

builder.Services.AddScoped<JwtIssuer>();
builder.Services.AddScoped<MigrationPlanner>();
builder.Services.AddScoped<IPlanRefEvidenceReader, PostgresPlanRefEvidenceReader>();
builder.Services.AddScoped<MigrationPlanReviewService>();
builder.Services.AddScoped<IMigrationPlanDraftRepository, PostgresMigrationPlanDraftRepository>();
builder.Services.AddScoped<MigrationPlanDraftService>();
builder.Services.AddScoped<GitHubTargetPreflightService>();
builder.Services.AddScoped<GitHubDestinationAccessService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IConnectionRepository, PostgresConnectionRepository>();
builder.Services.AddScoped<ConnectionService>();
builder.Services.AddScoped<IDiscoveryRepository, PostgresDiscoveryRepository>();
builder.Services.AddScoped<DiscoveryService>();
builder.Services.AddScoped<PostgresDiscoveryPageCheckpointStore>();
builder.Services.AddScoped<IDiscoveryPageCheckpointStore>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryRefIngestProgress>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryIdentityObservationStore>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryGroupObservationStore>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryMembershipEvidenceVerifier>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryAclEvidenceStore>(
    provider => provider.GetRequiredService<PostgresDiscoveryPageCheckpointStore>());
builder.Services.AddScoped<IDiscoveryRefManifestStore, PostgresDiscoveryRefManifestStore>();
builder.Services.AddScoped<IStagingRepository, PostgresStagingRepository>();
builder.Services.AddScoped<StagingSyncService>();
builder.Services.AddScoped<IMigrationJobRepository, PostgresMigrationJobRepository>();
builder.Services.AddScoped<MigrationJobService>();
builder.Services.AddScoped<MigrationJobProcessor>();
builder.Services.AddHostedService<MigrationJobBackgroundWorker>();
// F-068: the API always uses PostgreSQL clock authority for the
// F-026 queue; only explicit deterministic test fixtures may opt out.
builder.Services.AddScoped<IIncrementalPreparationRepository>(sp =>
    new PostgresIncrementalPreparationRepository(
        sp.GetRequiredService<AppDbContext>(),
        sp.GetRequiredService<TimeProvider>(),
        useDatabaseClock: true));
builder.Services.AddSingleton(new IncrementalPreparationQueueHealthOptions(
    builder.Configuration.GetValue("IncrementalQueueHealth:Enabled", false)));
builder.Services.AddScoped<IIncrementalPreparationQueueHealthReader,
    PostgresIncrementalPreparationQueueHealthReader>();
builder.Services.AddScoped<IIncrementalPreparationPhaseHealthReader,
    PostgresIncrementalPreparationPhaseHealthReader>();
builder.Services.AddScoped<IncrementalPreparationService>();
builder.Services.AddScoped<IIncrementalReviewRepository, PostgresIncrementalReviewRepository>();
builder.Services.AddScoped<IncrementalReviewService>();
builder.Services.AddScoped<IIncrementalTargetPreflightRepository,
    PostgresIncrementalTargetPreflightRepository>();
builder.Services.AddScoped<IncrementalTargetPreflightService>();
builder.Services.AddScoped<IIncrementalOperationPlanRepository,
    PostgresIncrementalOperationPlanRepository>();
builder.Services.AddScoped<IncrementalOperationPlanService>();
// F-097c.4c.0c: authenticated superadmin-only checkpoint human
// review. The default is OFF and neither endpoint calls GitHub.
builder.Services.AddSingleton(new GitCheckpointTargetHumanReviewOptions(
    builder.Configuration.GetValue("GitCheckpointTargetHumanReview:Enabled", false)));
// Independent prerequisites remain OFF unless explicitly enabled.
// The ledger reads only trusted F-014 source evidence, never request OIDs.
builder.Services.AddSingleton(new GitCheckpointTargetReReviewOptions(
    builder.Configuration.GetValue("GitCheckpointTargetReReview:Enabled", false)));
builder.Services.AddSingleton(new GitHubSourceCheckpointGitRefLedgerOptions(
    builder.Configuration.GetValue("GitHubSourceCheckpointGitRefLedger:Enabled", false)));
builder.Services.AddScoped<PostgresGitHubSourceCheckpointGitRefLedger>();
builder.Services.AddScoped<PostgresGitCheckpointTargetReReviewPrerequisites>();
builder.Services.AddScoped<PostgresGitCheckpointTargetHumanReviewJournal>();
// #109 source-locked read preflight is NOT provider GET authorization.
builder.Services.AddSingleton(new GitCheckpointTargetReadPreflightOptions(
    builder.Configuration.GetValue("GitCheckpointTargetReadPreflight:Enabled", false)));
builder.Services.AddScoped<PostgresGitCheckpointTargetReadPreflight>();
// #109 explicit second switch. Never enabled by the human decision
// or read-preflight switch; GET transport and immutable F-031 stay OFF.
builder.Services.AddSingleton(new GitCheckpointTargetObservationOptions(
    builder.Configuration.GetValue("GitCheckpointTargetObservation:Enabled", false)));
builder.Services.AddScoped<PostgresGitCheckpointTargetObservationJournal>();
// #104 read-only v2 F-091/F-097b + checkpoint F-031 admission; no writer.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointAuditOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointAudit:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointAuditPreflight>();
// #104: independent default-OFF source-locked v2 writeblocked records.
// No API endpoint, background dispatch, GitHub GET/write or F-035.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointWorkpackOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointWorkpack:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointWorkpackJournal>();
// #111 is an entirely separate default-OFF GET-only historical reader.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointHistoryOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointHistory:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointWorkpackHistory>();
// #112: internal F-035v2 evidence read only; no local acquisition permission.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointBundleReadOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointBundleRead:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointBundleReadPreflight>();
// F-035v2 opt-in local-only rehearsal; no HTTP/worker/DB bundle receipt.
builder.Services.AddScoped<IGitIncrementalLaterCheckpointBundleReadPreflight>(
    sp => sp.GetRequiredService<PostgresGitIncrementalLaterCheckpointBundleReadPreflight>());
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointLocalBundleOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointLocalBundle:Enabled", false)));
builder.Services.AddScoped<GitIncrementalLaterCheckpointLocalBundleAcquisition>();
// #113 source-first durable lease coordination is independently default-OFF.
// No public API, local runner dispatch, DB bundle evidence or GitHub write.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointBundleLeaseOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointBundleLease:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointBundleLeaseStore>();
// #113 final local artifact writer is a third, separately OFF feature.
// No route, worker, import admission or destination GitHub authority.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointDurableBundleOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointDurableBundle:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointBundleHistory>();
// #100: non-authorizing F-060v2 staging readback of genuine later F-035v2.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointMirrorStagingOptions(
    builder.Configuration.GetValue("GitIncrementalLaterCheckpointMirrorStaging:Enabled", false)));
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>();
// #117 independently default-OFF: source-first SQL reviewed read witness.
// This creates NO F-061 lease, no Git subprocess and no HTTP endpoint.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointMirrorAdmissionOptions(
    builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorAdmission:Enabled", false)));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>();
// #118 internal-only F-061v2 claim and renew coordinator. Must be
// deliberately opted in, independently of the non-authorizing preflight.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointMirrorLeaseOptions(
    builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorLease:Enabled", false)));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
// #119 separately default-OFF F-061v2 Verified journal. Physical bare
// Git audit is bound ONLY to server-selected trusted local roots.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointMirrorVerifiedOptions(
    builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorVerified:Enabled", false)));
builder.Services.AddScoped<IGitIncrementalLaterCheckpointMirrorPhysicalAuditor>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
        builder.Configuration["GitSourceBundles:BundleRoot"] ?? "",
        builder.Configuration[
            "GitIncrementalLaterCheckpointMirrorVerified:MirrorRoot"] ?? "",
        enabled: builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorVerified:PhysicalAuditEnabled", false),
        clock: sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal>();
// #121 quarantine is a READ-only diagnostic by default: no reviewer,
// no SQL write, no orphan adoption or generation re-admission.
builder.Services.AddSingleton(
    new GitIncrementalLaterCheckpointMirrorRecoveryInspectionOptions(
        builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorRecoveryInspection:Enabled",
            false)));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector>();
// #122: the human REVIEW journal is independently disabled. An
// authenticated superadmin may only record an immutable, non-authorizing
// decision. No orphan rename, lease release or new generation.
builder.Services.AddSingleton(
    new GitIncrementalLaterCheckpointMirrorRecoveryReviewOptions(
        builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorRecoveryReview:Enabled",
            false)));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal>();
// #123: prepared durable intent only; independently default-OFF,
// no physical mover, remote provider, generation or HTTP route.
builder.Services.AddSingleton(
    new GitIncrementalLaterCheckpointMirrorIsolationIntentOptions(
        builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorIsolationIntent:Enabled",
            false)));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorIsolationIntentJournal>();
// #124: source-first crash-reconciliation lookup of existing immutable
// PRE-MOVE evidence. No physical mutation, execution API or recovery
// authorization. Shares the separate default-OFF #123 option.
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader>();
// #124: physically verified quarantine, independently default-OFF.
// Even with its switch enabled this is an INTERNAL service: there is
// no route, hosted job, destination push or automatic re-admission.
// Real local Git and the final SQL write share the exact F-060 POSIX
// root lock, while the SQL transaction itself never executes Git.
builder.Services.AddSingleton(
    new GitIncrementalLaterCheckpointMirrorIsolationCoordinatorOptions(
        builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorIsolationCoordinator:Enabled",
            false)));
builder.Services.AddScoped<
    IGitIncrementalLaterCheckpointMirrorPhysicalIsolationSession>(sp =>
        new GitHubMigrator.Infrastructure.GitObjects.LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
            builder.Configuration["GitSourceBundles:BundleRoot"] ?? "",
            builder.Configuration[
                "GitIncrementalLaterCheckpointMirrorVerified:MirrorRoot"] ?? "",
            enabled: builder.Configuration.GetValue(
                "GitIncrementalLaterCheckpointMirrorIsolationCoordinator:PhysicalEnabled",
                false),
            clock: sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<
    PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator>();
// #120: dedicated local-only F-060v2 importer, separated from the
// historic baseline GitHubTrustedRemoteRead path. Both this import and
// its worker need independent OFF-by-default feature switches.
builder.Services.AddSingleton(new GitIncrementalLaterCheckpointMirrorImportWorkerOptions(
    Enabled: builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorImportWorker:Enabled", false),
    LeaseDuration: TimeSpan.FromSeconds(builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorImportWorker:LeaseSeconds", 180)),
    HeartbeatInterval: TimeSpan.FromMilliseconds(builder.Configuration.GetValue(
        "GitIncrementalLaterCheckpointMirrorImportWorker:HeartbeatMilliseconds",
        15000))));
builder.Services.AddScoped<IGitIncrementalLaterCheckpointMirrorImporter>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitSourceSnapshotMirrorMaterializer(
        builder.Configuration["GitSourceBundles:BundleRoot"] ?? "",
        builder.Configuration[
            "GitIncrementalLaterCheckpointMirrorVerified:MirrorRoot"] ?? "",
        sp.GetRequiredService<IGitSourceBundleArtifactVerifier>(),
        enabled: false,
        clock: sp.GetRequiredService<TimeProvider>(),
        laterCheckpointEnabled: builder.Configuration.GetValue(
            "GitIncrementalLaterCheckpointMirrorImportWorker:LocalImportEnabled", false)));
builder.Services.AddScoped<GitIncrementalLaterCheckpointMirrorImportWorker>();
builder.Services.AddScoped<PostgresGitIncrementalLaterCheckpointDurableBundleJournal>();
builder.Services.AddSingleton(new GitHubSourceSyncCheckpointOptions(
    builder.Configuration.GetValue("GitHubSourceSyncCheckpoints:Enabled", false)));
builder.Services.AddScoped<IGitHubSourceSyncCheckpointJournal,
    PostgresGitHubSourceSyncCheckpointJournal>();
// F-095: read-only internal persistence, completely disabled by default.
// Physical bare repositories are deployment-owned, never request paths.
builder.Services.AddSingleton(new GitIncrementalAncestryLedgerOptions(
    builder.Configuration.GetValue("GitIncrementalAncestryLedger:Enabled", false),
    RequireBoundSourceMirror: true));
builder.Services.AddScoped<ILocalGitIncrementalAncestryAuditor>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitIncrementalAncestryAuditor(
        builder.Configuration["GitIncrementalAncestryLedger:SourceRoot"] ?? "",
        builder.Configuration["GitIncrementalAncestryLedger:TargetRoot"] ?? "",
        builder.Configuration.GetValue("GitIncrementalAncestryLedger:PhysicalAuditEnabled", false),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IGitIncrementalAncestryLedger,
    PostgresGitIncrementalAncestryLedger>();
builder.Services.AddScoped<PostgresGitHubTargetRefEvidenceStore>();
builder.Services.AddScoped<IGitHubTargetRefEvidenceStore>(
    service => service.GetRequiredService<PostgresGitHubTargetRefEvidenceStore>());
builder.Services.AddScoped<PostgresVerifiedSourceGitRefReader>();
builder.Services.AddScoped<PostgresIncrementalGitRefReconciliationStore>();
builder.Services.AddScoped<IIncrementalGitRefReconciliationStore>(
    x => x.GetRequiredService<PostgresIncrementalGitRefReconciliationStore>());
builder.Services.AddScoped<IVerifiedSourceGitRefReader>(
    provider => provider.GetRequiredService<PostgresVerifiedSourceGitRefReader>());
builder.Services.AddScoped<IGitSourceBundleEvidenceStore, PostgresGitSourceBundleEvidenceStore>();
builder.Services.AddScoped<IGitSourceBundleAcquirer>(provider =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitSourceBundleAcquirer(
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false)
            ? builder.Configuration["GitSourceBundles:SourceRoot"] ?? "" : "",
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false)
            ? builder.Configuration["GitSourceBundles:BundleRoot"] ?? "" : "",
        maxStoreBytes: builder.Configuration.GetValue<long>(
            "GitSourceBundles:MaxStorageBytes",
            GitHubMigrator.Infrastructure.GitObjects.LocalGitBundleArtifactStore.DefaultMaxStorageBytes)));
builder.Services.AddScoped<IAzureGitSourceLocationResolver, PostgresAzureGitSourceLocationResolver>();
builder.Services.AddScoped<IAzureGitMirrorFetcher>(_ =>
    new GitHubMigrator.Infrastructure.GitObjects.AzureDevOpsGitMirrorFetcher(
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false) &&
        builder.Configuration.GetValue("AzureGitFetch:Enabled", false)
            ? builder.Configuration["GitSourceBundles:SourceRoot"] ?? ""
            : ""));
builder.Services.AddScoped<AzureGitSourceAcquisitionService>();
builder.Services.AddScoped<IAzureGitSourceAcquisition>(provider =>
    provider.GetRequiredService<AzureGitSourceAcquisitionService>());
builder.Services.AddScoped<IAzureGitFetchJobRepository, PostgresAzureGitFetchJobRepository>();
builder.Services.AddScoped<AzureGitFetchJobService>();
builder.Services.AddScoped<AzureGitFetchJobProcessor>();
builder.Services.AddHostedService<AzureGitFetchBackgroundWorker>();
builder.Services.AddScoped<IGitSourceBundleArtifactVerifier>(_ =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitBundleArtifactStore(
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false)
            ? builder.Configuration["GitSourceBundles:BundleRoot"] ?? "" : "",
        maxStorageBytes: builder.Configuration.GetValue<long>(
            "GitSourceBundles:MaxStorageBytes",
            GitHubMigrator.Infrastructure.GitObjects.LocalGitBundleArtifactStore.DefaultMaxStorageBytes)));
builder.Services.AddScoped<IGitSourceBundleOrphanQuarantine>(sp =>
    (IGitSourceBundleOrphanQuarantine)
    sp.GetRequiredService<IGitSourceBundleArtifactVerifier>());
builder.Services.AddScoped<IGitHubImportReadinessStore, PostgresGitHubImportReadinessStore>();
builder.Services.AddScoped<GitHubImportReadinessService>();
builder.Services.AddScoped<IGitHubImportIntentStore, PostgresGitHubImportIntentStore>();
builder.Services.AddScoped<GitHubImportIntentService>();
builder.Services.AddScoped<IGitImportRehearsalStore, PostgresGitImportRehearsalStore>();
builder.Services.AddScoped<GitImportRehearsalService>();
builder.Services.AddScoped<IGitHubRemoteRefFenceStore, PostgresGitHubRemoteRefFenceStore>();
builder.Services.AddScoped<GitHubRemoteRefFenceService>();
builder.Services.AddSingleton(new GitHubAppWriteCredentialOptions(
    builder.Configuration.GetValue("GitHubAppWriteCredential:Enabled", false),
    builder.Configuration.GetValue("GitHubAppWriteCredential:AppId", 0L),
    builder.Configuration.GetValue("GitHubAppWriteCredential:InstallationId", 0L),
    builder.Configuration["GitHubAppWriteCredential:PrivateKeySecretReference"] ?? "",
    builder.Configuration["GitHubAppWriteCredential:AllowedRepository"] ?? ""));
builder.Services.AddScoped<IGitHubAppWriteScopeStore, PostgresGitHubAppWriteScopeStore>();
builder.Services.AddScoped<GitHubAppWriteScopeService>();
builder.Services.AddScoped<GitHubCreateOnlyPacketService>();
// F-079: trusted GET-only remote sandbox review, still WRITE-DISABLED.
builder.Services.AddScoped<IGitHubCreateOnlyPacketEvidenceReader>(
    sp => sp.GetRequiredService<GitHubCreateOnlyPacketService>());
builder.Services.AddScoped<IGitHubRemoteDispatchAuthorityVerifier,
    PostgresGitHubRemoteDispatchReviewVerifier>();
builder.Services.AddScoped<PostgresGitHubRemoteSandboxReviewSessionJournal>();
builder.Services.AddScoped<PostgresGitHubRemoteSandboxDispatchIntentJournal>();
builder.Services.AddScoped<PostgresGitHubRemoteSandboxDispatchConfirmationJournal>();
builder.Services.AddSingleton(new GitHubRemoteTypedRecoveryBaselineOptions(
    builder.Configuration.GetValue("GitHubRemoteTypedRecoveryBaseline:Enabled", false),
    builder.Configuration.GetValue<long>(
        "GitHubRemoteTypedRecoveryBaseline:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteTypedRecoveryBaseline:AllowedRepository"] ?? ""));
builder.Services.AddScoped<PostgresGitHubRemoteTypedRecoveryBaselineJournal>();
builder.Services.AddScoped<IGitHubRemoteTypedRecoveryBaselineJournal>(
    sp => sp.GetRequiredService<PostgresGitHubRemoteTypedRecoveryBaselineJournal>());
builder.Services.AddSingleton(new GitHubSandboxOneShotOptions(
    builder.Configuration.GetValue("GitHubSandboxOneShot:Enabled", false),
    builder.Configuration.GetValue<long>("GitHubSandboxOneShot:AllowedRepositoryId", 0),
    builder.Configuration["GitHubSandboxOneShot:AllowedOrganization"] ?? "",
    builder.Configuration["GitHubSandboxOneShot:AllowedRepository"] ?? ""));
// F-083 has NO public API, job queue or hosted worker. This composition
// is a disabled-by-default internal sender for explicitly permitted sandbox
// experiments only; F-082 always marks possible transmission first.
builder.Services.AddScoped<PostgresGitHubRemoteSandboxPreSendBarrierJournal>();
builder.Services.AddScoped<PostgresGitHubRemotePostSendRecoveryJournal>();
builder.Services.AddSingleton(new GitHubRemoteRecoveryWorkbenchOptions(
    builder.Configuration.GetValue("GitHubRemoteRecoveryWorkbench:Enabled", false),
    builder.Configuration.GetValue<long>(
        "GitHubRemoteRecoveryWorkbench:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteRecoveryWorkbench:AllowedRepository"] ?? ""));
builder.Services.AddScoped<PostgresGitHubRemoteRecoveryWorkbenchReader>();
builder.Services.AddScoped<IGitHubRemoteRecoveryHistoryReader>(
    sp => sp.GetRequiredService<PostgresGitHubRemoteRecoveryWorkbenchReader>());
builder.Services.AddSingleton(new GitHubMultiRepositorySyncPulseOptions(
    builder.Configuration.GetValue("GitHubMultiRepositorySyncPulse:Enabled", false)));
builder.Services.AddScoped<PostgresGitHubMultiRepositorySyncPulseReader>();
builder.Services.AddScoped<IGitHubSandboxPreSendAuthority,
    PostgresGitHubSandboxPreSendAuthority>();
builder.Services.AddScoped<IGitHubSandboxOneShotGitWriter>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.GitHubSandboxCreateOnlyPushRunner(
        builder.Configuration["GitSourceBundles:BundleRoot"] ?? "",
        builder.Configuration["GitHubSandboxOneShot:ScratchRoot"] ?? "",
        sp.GetRequiredService<GitHubSandboxOneShotOptions>()));
builder.Services.AddScoped<GitHubSandboxOneShotSender>();
// F-087 is a separate, explicitly enabled, GET-only recovery capability.
// It never depends on the F-083 sender switch or remote writer.
builder.Services.AddSingleton(new GitHubIndependentRecoveryOptions(
    builder.Configuration.GetValue("GitHubIndependentRecovery:Enabled", false),
    builder.Configuration.GetValue<long>(
        "GitHubIndependentRecovery:AllowedRepositoryId", 0),
    builder.Configuration["GitHubIndependentRecovery:AllowedRepository"] ?? ""));
builder.Services.AddSingleton(new GitHubIndependentRecoveryCredentialOptions(
    builder.Configuration.GetValue("GitHubIndependentRecovery:ReadCredentialsEnabled", false),
    builder.Configuration.GetValue<long>("GitHubIndependentRecovery:AppId", 0),
    builder.Configuration.GetValue<long>("GitHubIndependentRecovery:InstallationId", 0),
    builder.Configuration["GitHubIndependentRecovery:PrivateKeySecretReference"] ?? "",
    builder.Configuration["GitHubIndependentRecovery:AllowedRepository"] ?? ""));
builder.Services.AddScoped<GitHubIndependentRecoveryAcquisition>();
builder.Services.AddScoped<IGitHubRemoteRecoveredReadbackJournal,
    PostgresGitHubRemoteRecoveredReadbackJournal>();
builder.Services.AddScoped<IGitHubRemoteSandboxReviewSessionRecorder>(
    sp => sp.GetRequiredService<PostgresGitHubRemoteSandboxReviewSessionJournal>());
builder.Services.AddScoped<GitHubRemoteTrustedDispatchReadOnlyOrchestrator>();
builder.Services.AddScoped<GitHubCandidatePolicyService>();
builder.Services.AddSingleton(new GitHubTransferAttemptOptions(
    builder.Configuration.GetValue("GitHubTransferAttempts:Enabled", false)));
builder.Services.AddScoped<IGitHubTransferAttemptStore, PostgresGitHubTransferAttemptStore>();
builder.Services.AddScoped<GitHubTransferAttemptService>();
builder.Services.AddSingleton(new GitHubRemoteSandboxFenceOptions(
    builder.Configuration.GetValue("GitHubRemoteSandboxFence:Enabled", false),
    builder.Configuration.GetValue<long>("GitHubRemoteSandboxFence:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteSandboxFence:AllowedRepository"] ?? ""));
builder.Services.AddScoped<IGitHubRemoteSandboxFenceStore, PostgresGitHubRemoteSandboxFenceStore>();
builder.Services.AddScoped<GitHubRemoteSandboxFenceService>();
builder.Services.AddScoped<IGitHubRemoteAppPreflightEvidenceJournal, PostgresGitHubRemoteAppPreflightEvidenceJournal>();
builder.Services.AddScoped<GitHubRemoteAppPreflightService>();
builder.Services.AddSingleton(new GitHubRemoteTestGrantOptions(
    builder.Configuration.GetValue("GitHubRemoteTestGrant:Enabled", false),
    builder.Configuration.GetValue<long>("GitHubRemoteTestGrant:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteTestGrant:AllowedRepository"] ?? ""));
builder.Services.AddScoped<IGitHubRemoteTestGrantStore, PostgresGitHubRemoteTestGrantStore>();
builder.Services.AddScoped<IGitHubRemoteReadbackJournal, PostgresGitHubRemoteReadbackJournal>();
builder.Services.AddSingleton(new GitHubRemoteTransmitHazardOptions(
    builder.Configuration.GetValue("GitHubRemoteTransmitHazard:Enabled", false),
    builder.Configuration.GetValue<long>("GitHubRemoteTransmitHazard:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteTransmitHazard:AllowedRepository"] ?? ""));
builder.Services.AddScoped<IGitHubRemoteTransmitHazardJournal,
    PostgresGitHubRemoteTransmitHazardJournal>();
builder.Services.AddSingleton(new GitHubReadOnlyObjectMirrorOptions(
    builder.Configuration.GetValue("GitHubRemoteObjectReconciliation:Enabled", false),
    builder.Configuration.GetValue<long>("GitHubRemoteObjectReconciliation:AllowedRepositoryId", 0),
    builder.Configuration["GitHubRemoteObjectReconciliation:AllowedRepository"] ?? ""));
builder.Services.AddScoped<IGitHubRemoteObjectReconciliationJournal,
    PostgresGitHubRemoteObjectReconciliationJournal>();
builder.Services.AddSingleton(new GitHubTrustedRemoteReadOptions(
    builder.Configuration.GetValue("GitHubTrustedRemoteRead:Enabled", false)));
builder.Services.AddScoped<IGitHubTrustedRemoteReadPlanReader,
    PostgresGitHubTrustedRemoteReadPlanReader>();
builder.Services.AddScoped<IGitHubReadOnlyObjectMirrorRunner>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.GitHubReadOnlyObjectMirrorRunner(
        builder.Configuration["GitHubTrustedRemoteRead:SourceRoot"] ?? "",
        builder.Configuration["GitHubTrustedRemoteRead:ScratchRoot"] ?? "",
        sp.GetRequiredService<GitHubReadOnlyObjectMirrorOptions>(),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(new GitSourceMirrorLifecycleOptions(
    builder.Configuration.GetValue("GitHubSourceMirrorLifecycle:Enabled", false)));
builder.Services.AddScoped<IGitSourceMirrorLifecycleJournal,
    PostgresGitSourceMirrorLifecycleJournal>();
builder.Services.AddScoped<IGitSourceMirrorFleetHealthReader,
    PostgresGitSourceMirrorFleetHealthReader>();
builder.Services.AddScoped<GitHubMigrator.Infrastructure.GitObjects.LocalGitSourceSnapshotMirrorMaterializer>(sp =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitSourceSnapshotMirrorMaterializer(
        builder.Configuration["GitSourceBundles:BundleRoot"] ?? "",
        builder.Configuration["GitHubTrustedRemoteRead:SourceRoot"] ?? "",
        sp.GetRequiredService<IGitSourceBundleArtifactVerifier>(),
        builder.Configuration.GetValue("GitHubSourceSnapshotMirror:Enabled", false)
            && builder.Configuration.GetValue("GitSourceBundles:Enabled", false),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IGitSourceSnapshotMirrorMaterializer>(sp =>
    new DurableGitSourceSnapshotMirrorMaterializer(
        sp.GetRequiredService<GitHubMigrator.Infrastructure.GitObjects.LocalGitSourceSnapshotMirrorMaterializer>(),
        sp.GetRequiredService<IGitSourceMirrorLifecycleJournal>()));
builder.Services.AddScoped<GitHubTrustedRemoteReadCoordinator>();
builder.Services.AddScoped<IGitHubTrustedRemoteReadExecutor>(
    sp => sp.GetRequiredService<GitHubTrustedRemoteReadCoordinator>());

builder.Services.AddScoped<GitHubRemoteTestGrantService>();
builder.Services.AddHostedService<GitHubRemoteTestGrantBackgroundWorker>();
builder.Services.AddHostedService<GitHubRemoteSandboxFenceBackgroundWorker>();
builder.Services.AddHostedService<GitHubTransferAttemptBarrierWorker>();
var localSandboxIds = (builder.Configuration["LocalGitSandboxDispatch:AllowedSandboxIds"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
    .Where(x => x != Guid.Empty).ToHashSet();
builder.Services.AddSingleton(new GitSandboxDispatchOptions(
    builder.Configuration.GetValue("LocalGitSandboxDispatch:Enabled", false),
    localSandboxIds));
builder.Services.AddScoped<IGitSandboxDispatchStore, PostgresGitSandboxDispatchStore>();
builder.Services.AddScoped<GitSandboxDispatchService>();
builder.Services.AddScoped<ILocalGitCreateOnlyTransferRunner>(_ =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitCreateOnlyTransferRunner(
        builder.Configuration.GetValue("LocalGitSandboxDispatch:Enabled", false)
            ? builder.Configuration["GitSourceBundles:BundleRoot"] ?? "" : "",
        builder.Configuration.GetValue("LocalGitSandboxDispatch:Enabled", false)
            ? builder.Configuration["LocalGitSandboxDispatch:DestinationRoot"] ?? "" : "",
        builder.Configuration.GetValue("LocalGitSandboxDispatch:Enabled", false)
            ? builder.Configuration["LocalGitSandboxDispatch:ScratchRoot"] ?? "" : "",
        enabled: builder.Configuration.GetValue("LocalGitSandboxDispatch:Enabled", false)));
builder.Services.AddScoped<GitSandboxDispatchProcessor>();
builder.Services.AddHostedService<GitSandboxDispatchBackgroundWorker>();
builder.Services.AddSingleton(new GitHubTargetPreviewLeaseOptions(
    builder.Configuration.GetValue("GitHubTargetPreviewLeases:Enabled", false)));
builder.Services.AddScoped<IGitHubTargetPreviewLeaseStore, PostgresGitHubTargetPreviewLeaseStore>();
builder.Services.AddScoped<GitHubTargetPreviewLeaseService>();
builder.Services.AddScoped<IGitImportRehearsalRunner>(_ =>
    new GitHubMigrator.Infrastructure.GitObjects.LocalGitImportRehearsalRunner(
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false)
            ? builder.Configuration["GitSourceBundles:BundleRoot"] ?? "" : "",
        builder.Configuration.GetValue("GitImportRehearsal:Enabled", false)
            ? builder.Configuration["GitImportRehearsal:ScratchRoot"] ?? "" : "",
        builder.Configuration.GetValue("GitImportRehearsal:Enabled", false) &&
        builder.Configuration.GetValue("GitSourceBundles:Enabled", false)));
builder.Services.AddScoped<GitSourceBundleService>();
builder.Services.AddScoped<IGitTransferWorkpackRepository, PostgresGitTransferWorkpackRepository>();
builder.Services.AddScoped<GitTransferWorkpackService>();
builder.Services.AddScoped<GitTransferWorkpackProcessor>();
builder.Services.AddHostedService<GitTransferWorkpackBackgroundWorker>();
builder.Services.AddScoped<IncrementalGitRefReconciliationService>();
builder.Services.AddScoped<GitHubTargetRefEvidenceService>();
builder.Services.AddScoped<IncrementalPreparationProcessor>();
builder.Services.AddHostedService<IncrementalPreparationBackgroundWorker>();
builder.Services.AddScoped<IIdentitySnapshotReader, PostgresIdentitySnapshotReader>();
builder.Services.AddScoped<IIdentityMappingDraftRepository, PostgresIdentityMappingDraftRepository>();
builder.Services.AddScoped<IdentityMappingService>();
builder.Services.AddScoped<WorkloadPreviewService>();
builder.Services.AddScoped<BoardReconciliationService>();
builder.Services.AddHttpClient<GitHubDestinationAccessReader>(c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubDestinationAccessReader>(s => s.GetRequiredService<GitHubDestinationAccessReader>());
// Fixed-host, bounded GET-only F-046 policy review. No auto redirects.
builder.Services.AddHttpClient<GitHubCandidatePolicyReader>(
        c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubCandidatePolicyReader>(
    p => p.GetRequiredService<GitHubCandidatePolicyReader>());
builder.Services.AddHttpClient<GitHubAppScopedWriteCredentialInspector>(
        c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubAppScopedWriteCredentialInspector>(
    p => p.GetRequiredService<GitHubAppScopedWriteCredentialInspector>());
builder.Services.AddTransient<IGitHubRemoteAppPreflightReader>(
    p => p.GetRequiredService<GitHubAppScopedWriteCredentialInspector>());
builder.Services.AddTransient<IGitHubSandboxVerifiedTokenScope>(
    p => p.GetRequiredService<GitHubAppScopedWriteCredentialInspector>());
builder.Services.AddTransient<IGitHubRecoveryReadOnlyTokenScope>(
    p => p.GetRequiredService<GitHubAppScopedWriteCredentialInspector>());
builder.Services.AddHttpClient<GitHubRemoteTipObjectReader>(
        c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubRemoteTipObjectReader>(
    p => p.GetRequiredService<GitHubRemoteTipObjectReader>());
builder.Services.AddHttpClient<GitHubImportPolicyReader>(c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubImportPolicyReader>(
    provider => provider.GetRequiredService<GitHubImportPolicyReader>());
builder.Services.AddHttpClient<GitHubImportRepositoryReader>(c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubImportRepositoryReader>(
    provider => provider.GetRequiredService<GitHubImportRepositoryReader>());
builder.Services.AddHttpClient<GitHubTargetRepositoryProbe>(c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubTargetRepositoryProbe>(s => s.GetRequiredService<GitHubTargetRepositoryProbe>());
builder.Services.AddHttpClient<GitHubRefObservationReader>(
        c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubRefObservationReader>(
    s => s.GetRequiredService<GitHubRefObservationReader>());
builder.Services.AddHttpClient<GitHubTraceabilityReader>(c => c.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IGitHubTraceabilityReader>(s => s.GetRequiredService<GitHubTraceabilityReader>());
builder.Services.AddHttpClient<AzureDevOpsWorkloadReader>(c => c.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<GitHubConfigurationReader>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<FoundryPipelineReviewer>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IAzureWorkloadReader>(s => s.GetRequiredService<AzureDevOpsWorkloadReader>());
builder.Services.AddTransient<IGitHubConfigurationReader>(s => s.GetRequiredService<GitHubConfigurationReader>());
builder.Services.AddTransient<IPipelineAiReviewer>(s => s.GetRequiredService<FoundryPipelineReviewer>());
builder.Services.AddHttpClient<AzureDevOpsDiscoveryReader>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IDiscoveryReader>(s => s.GetRequiredService<AzureDevOpsDiscoveryReader>());
builder.Services.AddHttpClient<AzureDevOpsConnectionValidator>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<GitHubConnectionValidator>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddTransient<IConnectionValidator>(s => s.GetRequiredService<AzureDevOpsConnectionValidator>());
builder.Services.AddTransient<IConnectionValidator>(s => s.GetRequiredService<GitHubConnectionValidator>());

var credentialProvider = builder.Configuration["Secrets:Provider"] ??
    (builder.Environment.IsDevelopment() ? "EncryptedPostgreSql" : "KeyVault");
if (credentialProvider.Equals("KeyVault", StringComparison.OrdinalIgnoreCase))
{
    var vaultUri = builder.Configuration["Secrets:VaultUri"] ??
        throw new InvalidOperationException("Secrets:VaultUri is required for KeyVault.");
    builder.Services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
    builder.Services.AddScoped<IConnectionSecretStore, KeyVaultConnectionSecretStore>();
}
else if (credentialProvider.Equals("EncryptedPostgreSql", StringComparison.OrdinalIgnoreCase) &&
    builder.Environment.IsDevelopment())
{
    var encryptionKey = builder.Configuration["Secrets:LocalEncryptionKey"] ??
        throw new InvalidOperationException("Secrets:LocalEncryptionKey is required for local development.");
    builder.Services.AddScoped<IConnectionSecretStore>(s =>
        new PostgresEncryptedConnectionSecretStore(s.GetRequiredService<AppDbContext>(), encryptionKey));
}
else throw new InvalidOperationException("Local encrypted credential storage is restricted to Development.");
var storageConnection = builder.Configuration["Storage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(storageConnection))
{
    builder.Services.AddSingleton<IArtifactStorage>(_ => new BlobArtifactStorage(
        new BlobContainerClient(storageConnection, builder.Configuration["Storage:Container"] ?? "migration-artifacts")));
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "GitHubMigrator",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "GitHubMigrator.Web",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("PasswordChanged", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("password_changed", "true")));

var frontendOrigin = builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy => policy.WithOrigins(frontendOrigin).AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("external", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Isolated provider lookup budget: preflight must not starve migration/Boards/Pipelines.
    options.AddFixedWindowLimiter("target-preflight", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("staging-sync", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Separate bounded budget: multi-step human review cannot exhaust the
    // snapshot staging limiter or starve migration queues.
    options.AddFixedWindowLimiter("incremental-review", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // F-066: a separate limited budget for read-only queue aggregates.
    // These reads must not starve active preparation/review endpoints.
    options.AddFixedWindowLimiter("incremental-queue-health", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // F-039 GET-only GitHub metadata review: independent small provider
    // budget so it cannot starve existing F-027/F-038 human review routes.
    options.AddFixedWindowLimiter("import-readiness", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Independently bounded F-040 intent creation/policy reads and human decisions.
    options.AddFixedWindowLimiter("import-intent", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("git-rehearsal", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-remote-fence", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-app-scope", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-create-packet", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-preview-lease", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // F-065 diagnostics read an aggregate over the lifecycle table.
    // Keep a separate bounded budget from migration and provider work.
    options.AddFixedWindowLimiter("source-mirror-health", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-candidate-policy", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-transfer-attempts", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-remote-sandbox-fence", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-remote-app-preflight", limiter =>
    {
        limiter.PermitLimit = 4;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("github-remote-test-grant", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Independent, low-volume provider GET + immutable audit budget.
    options.AddFixedWindowLimiter("github-remote-sandbox-session", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // F-080: independent budget for strictly database-only inert intents.
    options.AddFixedWindowLimiter("github-remote-sandbox-dispatch-intent", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // F-081: separate per-minute budget for independent confirmation.
    options.AddFixedWindowLimiter("github-remote-sandbox-dispatch-confirmation", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("git-local-sandbox-dispatch", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    // Enqueueing work is cheap; it must not consume the shared external-API
    // budget used by Boards, Pipelines and legacy synchronous discovery.
    options.AddFixedWindowLimiter("discovery-queue", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("GitHubMigrator.Api"))
    .WithTracing(trace => trace.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
if (builder.Configuration["Observability:Provider"]?.Equals("AzureMonitor", StringComparison.OrdinalIgnoreCase) == true)
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        throw new InvalidOperationException("APPLICATIONINSIGHTS_CONNECTION_STRING is required for AzureMonitor.");
    telemetry.UseAzureMonitor(options =>
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
}
else
{
    telemetry.UseOtlpExporter();
}

var bootstrapPassword = builder.Configuration["Bootstrap:Password"];
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(bootstrapPassword) || bootstrapPassword == "superadmin"))
    throw new InvalidOperationException("A strong non-default Bootstrap:Password is required outside Development.");

var app = builder.Build();

app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await Bootstrap.InitializeAsync(app.Services, builder.Configuration, builder.Environment, app.Logger);
await GitTransferWorkpackSchema.InitializeAsync(app.Services);
await GitSourceBundleSchema.InitializeAsync(app.Services);
await GitHubImportReadinessSchema.InitializeAsync(app.Services);
await GitHubImportIntentSchema.InitializeAsync(app.Services);
await GitImportRehearsalSchema.InitializeAsync(app.Services);
await GitHubRemoteRefFenceSchema.InitializeAsync(app.Services);
await GitHubAppWriteScopeSchema.InitializeAsync(app.Services);
await GitHubTargetPreviewLeaseSchema.InitializeAsync(app.Services);
await GitHubTransferAttemptSchema.InitializeAsync(app.Services);
await GitSandboxDispatchSchema.InitializeAsync(app.Services);
await GitHubRemoteSandboxFenceSchema.InitializeAsync(app.Services);
await GitHubRemoteTestGrantSchema.InitializeAsync(app.Services);
await GitHubRemoteAppPreflightEvidenceSchema.InitializeAsync(app.Services);
await GitHubRemoteReadbackSchema.InitializeAsync(app.Services);
await GitHubRemoteTransmitHazardSchema.InitializeAsync(app.Services);
// F-078 has an FK to F-055: never initialize this before the hazard table.
await GitHubRemoteSandboxReviewSessionSchema.InitializeAsync(app.Services);
await GitHubRemoteSandboxDispatchIntentSchema.InitializeAsync(app.Services);
await GitHubRemoteSandboxDispatchConfirmationSchema.InitializeAsync(app.Services);
await GitHubRemoteSandboxPreSendBarrierSchema.InitializeAsync(app.Services);
await GitHubRemoteObjectReconciliationSchema.InitializeAsync(app.Services);
await GitHubRemotePostSendRecoverySchema.InitializeAsync(app.Services);
await GitHubRemoteTypedRecoveryBaselineSchema.InitializeAsync(app.Services);
await GitHubSourceSyncCheckpointSchema.InitializeAsync(app.Services);
await GitHubSourceCheckpointGitRefLedgerSchema.InitializeAsync(app.Services);
await GitIncrementalCheckpointWorkpackSchema.InitializeAsync(app.Services);
await GitCheckpointTargetHumanReviewSchema.InitializeAsync(app.Services);
await GitCheckpointTargetObservationSchema.InitializeAsync(app.Services);
await GitIncrementalLaterCheckpointWorkpackSchema.InitializeAsync(app.Services);
await GitIncrementalLaterCheckpointBundleEvidenceSchema.InitializeAsync(app.Services);
await GitIncrementalLaterCheckpointBundleLeaseSchema.InitializeAsync(app.Services);
// #118 installs constraint-only F-061v2 tables; does not enable workers.
await GitIncrementalLaterCheckpointMirrorLeaseSchema.InitializeAsync(app.Services);
// #119 installs only write-blocked physical Verified evidence tables.
await GitIncrementalLaterCheckpointMirrorVerifiedSchema.InitializeAsync(app.Services);
// #122: additive immutable reviewed-intent SQL; no recovery authority.
await GitIncrementalLaterCheckpointMirrorRecoveryDecisionSchema.InitializeAsync(app.Services);
// #123: constraint-only pre-move isolation intent and post-move journal;
// NOT a source-lock coordinator, filesystem actor, new lease or HTTP route.
await GitIncrementalLaterCheckpointMirrorIsolationSchema.InitializeAsync(app.Services);
// #127 installs audit-only generation assessment lineage after isolation.
// It never mints worker/token or reopens the original RecoveryRequired lease.
await GitIncrementalLaterCheckpointMirrorGenerationAssessmentSchema
    .InitializeAsync(app.Services);
// #129: empty, DB-enforced write-disabled second-review journals only.
await GitIncrementalLaterCheckpointMirrorSecondReviewSchema
    .InitializeAsync(app.Services);
// #128: AFTER all F-061 DDL, repair any previously installed CHECKs
// with doubled regex bounds from non-interpolated SQL raw strings.
await GitIncrementalLaterCheckpointMirrorRegexConstraintRepairSchema
    .InitializeAsync(app.Services);
await GitIncrementalAncestryLedgerSchema.InitializeAsync(app.Services);
await GitSourceMirrorLifecycleSchema.InitializeAsync(app.Services);
await AzureGitFetchJobSchema.InitializeAsync(app.Services);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "GitHubMigrator.Api" }));
app.MapGet("/api/ready", async Task<IResult> (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var auth = app.MapGroup("/api/auth");
auth.MapPost("/login", async Task<IResult> (LoginRequest request, UserManager<ApplicationUser> users,
    JwtIssuer jwt, ILogger<Program> logger) =>
{
    var user = await users.FindByNameAsync(request.Username ?? "");
    if (user is null || await users.IsLockedOutAsync(user))
        return Results.Unauthorized();
    if (!await users.CheckPasswordAsync(user, request.Password ?? ""))
    {
        await users.AccessFailedAsync(user);
        logger.LogWarning("Authentication rejected: invalid credentials");
        return Results.Unauthorized();
    }
    await users.ResetAccessFailedCountAsync(user);
    logger.LogInformation("Platform user authenticated UserId={UserId} MustChangePassword={MustChangePassword}",
        user.Id, user.MustChangePassword);
    return Results.Ok(await jwt.CreateAsync(user));
}).RequireRateLimiting("login");

auth.MapPost("/change-password", async Task<IResult> (ChangePasswordRequest request, ClaimsPrincipal principal,
    UserManager<ApplicationUser> users, JwtIssuer jwt, ILogger<Program> logger) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var result = await users.ChangePasswordAsync(user, request.CurrentPassword ?? "", request.NewPassword ?? "");
    if (!result.Succeeded)
        return Results.ValidationProblem(result.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
    user.MustChangePassword = false;
    await users.UpdateAsync(user);
    logger.LogInformation("Password rotated for platform user UserId={UserId}", user.Id);
    return Results.Ok(await jwt.CreateAsync(user));
}).RequireAuthorization();

app.MapPost("/api/users", async Task<IResult> (CreateUserRequest request, ClaimsPrincipal principal,
    UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles, ILogger<Program> logger) =>
{
    var actor = await users.GetUserAsync(principal);
    if (actor is null || actor.MustChangePassword) return Results.Forbid();
    var actorRoles = await users.GetRolesAsync(actor);
    var targetRole = request.Role?.ToLowerInvariant();
    if (targetRole is not ("superadmin" or "admin" or "user"))
        return Results.BadRequest(new { error = "Invalid role" });
    if (!actorRoles.Contains("superadmin") && targetRole != "user")
        return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.TemporaryPassword))
        return Results.BadRequest(new { error = "Username and temporary password are required." });
    var user = new ApplicationUser { UserName = request.Username.Trim(), MustChangePassword = true };
    var created = await users.CreateAsync(user, request.TemporaryPassword);
    if (!created.Succeeded)
        return Results.ValidationProblem(created.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
    await users.AddToRoleAsync(user, targetRole);
    logger.LogInformation("Platform user created UserId={UserId} Role={Role} ActorId={ActorId}", user.Id, targetRole, actor.Id);
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.UserName, role = targetRole, user.MustChangePassword });
}).RequireAuthorization("PasswordChanged").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    { Roles = "superadmin,admin" });

app.MapGet("/api/providers", (ConnectionService service) => Results.Ok(new
{
    available = service.SupportedProviders(),
    planned = new[] { "azure-devops-server", "tfs-server", "github-enterprise-server" }
})).RequireAuthorization("PasswordChanged");

app.MapConnectionEndpoints();
app.MapDiscoveryEndpoints();
app.MapStagingEndpoints();
app.MapIncrementalPreparationEndpoints();
app.MapGitHubTargetPreviewLeaseEndpoints();
app.MapGitSourceMirrorFleetHealthEndpoints();
app.MapIncrementalPreparationQueueHealthEndpoints();
app.MapGitHubCandidatePolicyEndpoints();
app.MapGitHubTransferAttemptEndpoints();
app.MapGitHubRemoteSandboxFenceEndpoints();
app.MapGitHubRemoteAppPreflightEndpoints();
app.MapGitHubRemoteTestGrantEndpoints();
app.MapGitHubRemoteSandboxReviewSessionEndpoints();
app.MapGitHubRemoteSandboxDispatchIntentEndpoints();
app.MapGitHubRemoteSandboxDispatchConfirmationEndpoints();
app.MapGitHubRemotePostSendRecoveryEndpoints();
app.MapGitHubRemoteTypedRecoveryBaselineEndpoints();
app.MapGitHubIndependentRecoveryEndpoints();
app.MapGitHubRemoteRecoveryWorkbenchEndpoints();
app.MapGitHubMultiRepositorySyncPulseEndpoints();
app.MapGitHubSourceSyncCheckpointEndpoints();
app.MapGitCheckpointTargetHumanReviewEndpoints();
app.MapGitIncrementalLaterCheckpointMirrorRecoveryEndpoints();
app.MapGitCheckpointTargetReadPreflightEndpoints();
app.MapGitCheckpointTargetObservationEndpoints();
app.MapGitIncrementalLaterCheckpointWorkpackHistoryEndpoints();
app.MapGitSandboxDispatchEndpoints();
app.MapMigrationJobEndpoints();
app.MapIdentityMappingEndpoints();
app.MapMigrationPlanEndpoints();
app.MapWorkloadEndpoints();

app.Run();

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Username, string? TemporaryPassword, string? Role);
public partial class Program { }
