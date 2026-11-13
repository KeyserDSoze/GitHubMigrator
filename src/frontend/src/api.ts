export type Session = { accessToken: string; mustChangePassword: boolean; username: string; role: string };

export type SourceSyncCheckpointDelta = {
  sourceRepositoryId: string; targetRepositoryName: string;
  added: number; changed: number; restored: number;
  missing: number; total: number;
};
export type SourceSyncCheckpointProof = {
  preparationId: string; planId: string; sourceConnectionId: string;
  snapshotId: string; baselineSnapshotId: string;
  previousCheckpointId: string | null;
  previousEvidenceSha256: string | null;
  manifestSha256: string; sourceCollectedAtUtc: string;
  stagingAppliedAtUtc: string; sourceInventoryComplete: boolean;
  isBaselineSnapshot: boolean;
  journalAdded: number; journalChanged: number;
  journalRestored: number; journalMissing: number;
  totalRefChanges: number; unmappedRefChanges: number;
  repositories: SourceSyncCheckpointDelta[];
};
export type SourceRefChangeManifest = {
  sourceConnectionId: string; snapshotId: string;
  added: number; changed: number; restored: number; missing: number;
  total: number; evidenceSha256: string;
};
export type SourceSyncCheckpointReceipt = {
  id: string; proof: SourceSyncCheckpointProof;
  evidenceSha256: string; recordedBy: string;
  recordedAtUtc: string; canPush: false; canRetry: false;
  canDispatch: false; migrationComplete: false;
  evidenceVersion: 1 | 2;
  refChangeManifest: SourceRefChangeManifest | null;
  refChangeAttestationSha256: string | null;
};
export type SourceSyncCheckpointPage = {
  preparationId: string; planId: string;
  items: SourceSyncCheckpointReceipt[];
  nextAfter: string | null; latestCheckpointId: string | null;
  nextEligibleSnapshotId: string | null;
  latestEvidenceSha256: string | null;
  canPush: false; canRetry: false; canDispatch: false;
  migrationComplete: false;
};

export type SourceCheckpointRefChange = {
  sourceId: string; sourceRepositoryId: string; refName: string;
  kind: 'Added' | 'Changed' | 'Restored' | 'Missing';
  previousFingerprint: string | null;
  currentFingerprint: string | null;
  revision: number; targetRepositoryName: string | null;
};
export type SourceCheckpointRefChangePage = {
  checkpointId: string; snapshotId: string;
  refManifestSha256: string; refAttestationSha256: string;
  totalRefChanges: number; items: SourceCheckpointRefChange[];
  nextAfter: string | null;
  canPush: false; canRetry: false; canDispatch: false;
  migrationComplete: false;
};

export type CheckpointTargetReviewItem = {
  sourceChange: SourceCheckpointRefChange;
  targetRefObserved: boolean;
  reviewState: string;
};
export type CheckpointTargetRefReviewPage = {
  checkpointId: string; sourceSnapshotId: string;
  targetObservationId: string; sourceRepositoryId: string;
  targetRepositoryName: string; sourceRefManifestSha256: string;
  sourceRefAttestationSha256: string; targetEvidenceSha256: string;
  targetObservedAtUtc: string; repositoryRefChanges: number;
  items: CheckpointTargetReviewItem[]; nextAfter: string | null;
  canPush: false; canRetry: false; canDispatch: false;
  migrationComplete: false;
};

// F-097c.4c.0c: safe projection of the independently verified later-epoch
// candidate and immutable audit decision. All authority fields are false.
export type CheckpointHumanReviewCandidate = {
  preparationId: string; planId: string; checkpointId: string;
  sourceSnapshotId: string; sourceRepositoryId: string;
  targetRepositoryName: string; evidenceSha256: string;
  canObserveTarget: false; canPush: false; canRetry: false;
  canDispatch: false; migrationComplete: false;
};
export type CheckpointHumanReviewReceipt = {
  id: string; evidenceVersion: 1;
  preparationId: string; planId: string; checkpointId: string;
  sourceSnapshotId: string; sourceRepositoryId: string;
  targetRepositoryName: string; candidateEvidenceSha256: string;
  requestedBy: string; reviewedBy: string;
  reviewedAtUtc: string; evidenceSha256: string;
  decision: 'ApprovedForObservation' | 'Rejected';
  canObserveTarget: false; canPush: false; canRetry: false;
  canDispatch: false; migrationComplete: false;
};

// #109 later-F-031 server-verified immutable GitHub GET (no raw refs,
// OIDs or credentials in browser response, no execution authority).
export type CheckpointTargetObservation = {
  id: string; evidenceVersion: 1;
  preparationId: string; planId: string; checkpointId: string;
  sourceSnapshotId: string; sourceRepositoryId: string;
  targetRepositoryName: string; organization: string;
  objectFormat: 'sha1' | 'sha256';
  candidateEvidenceSha256: string; humanReviewEvidenceSha256: string;
  targetEvidenceSha256: string; observedAtUtc: string; refCount: number;
  evidenceSha256: string;
  canObserveTarget: false; canPush: false; canRetry: false;
  canDispatch: false; migrationComplete: false;
};

// #111 strictly redacted history of an immutable later-epoch workpack.
// No ref/OID arrays, credential, filesystem path, actor or current permit.
export type LaterCheckpointWorkpackHistory = {
  evidenceVersion: 2; preparationId: string; planId: string;
  checkpointId: string; sourceSnapshotId: string;
  targetObservationId: string; reconciliationId: string;
  workpackId: string; evidenceSha256: string;
  reconciliationSha256: string; sourceFullRefProofSha256: string;
  targetReceiptSha256: string; recordedAtUtc: string;
  sourceRefCount: number; targetRefCount: number;
  matchingCount: number; createCandidateCount: number;
  divergedCount: number; targetOnlyCount: number;
  state: 'Blocked'; phase: 'WriteBlocked';
  historicalOnly: true; currentApprovalVerified: false;
  readyForExecution: false; canPush: false; canRetry: false;
  canDispatch: false; migrationComplete: false;
};

export type RepositorySyncSourceDelta = {
  sourceRepositoryId: string; targetRepositoryName: string;
  addedRefs: number; changedRefs: number; restoredRefs: number;
  missingRefs: number; plannedRefChanges: number;
  totalRefChanges: number;
  stepState: 'Blocked'; stepBlocker: string;
  recoveryTracking: 'NotMonitored' | 'NoMarkerRecorded' | 'PossibleSendEvidence';
  recoveryState: RemoteRecoveryState | null;
  latestBarrierId: string | null; latestPossibleSendUtc: string | null;
  canOpenRecovery: boolean; canPush: false; canRetry: false;
  migrationComplete: false;
};
export type MultiRepositorySyncPulse = {
  preparationId: string; planId: string; sourceConnectionId: string;
  snapshotId: string; organization: string; manifestSha256: string;
  planIsCurrent: boolean; readyForExecution: false;
  preparationState: string;
  stagingState: 'LatestRecordedSnapshot' | 'SupersededInPostgres';
  latestKnownSnapshotId: string;
  sourceCollectedAtUtc: string; stagingAppliedAtUtc: string;
  latestKnownStagingAppliedAtUtc: string; sourceInventoryComplete: boolean;
  stagingObserved: number; stagingAdded: number; stagingChanged: number;
  stagingUnchanged: number; stagingMarkedMissing: number;
  journalAdded: number; journalChanged: number; journalRestored: number;
  journalMissing: number; latestStageJobId: string | null;
  stageJobState: string; stageJobErrorCode: string | null;
  stageJobUpdatedAtUtc: string | null;
  repositories: RepositorySyncSourceDelta[];
  providerSourceFreshnessVerified: false;
  canPush: false; canRetry: false; migrationComplete: false;
};

export type RemoteRecoveryState =
  | 'AwaitingIndependentReadback'
  | 'OperatorReviewRequired'
  | 'TipsObservedNeedsClosure'
  | 'IndependentClosureObserved'
  | 'CheckpointedIndependentClosure';
export type RemoteRecoveryNextAction =
  | 'ObserveTipsReadOnly' | 'VerifyFullObjectsReadOnly'
  | 'CertifyIndependentClosure' | 'OperatorReviewRequired' | 'None';
export type RemoteRecoveryItem = {
  barrierId: string; confirmationId: string; intentId: string; sessionId: string;
  preparationId: string; planId: string; scopeId: string; hazardId: string;
  grantId: string; repositoryId: number; organization: string; repository: string;
  possibleSendRecordedAtUtc: string; state: RemoteRecoveryState;
  recommendedNextAction: RemoteRecoveryNextAction; needsOperatorAttention: boolean;
  typedBaselineAvailable: boolean; baselineRefCount: number | null;
  createCandidateCount: number | null; barrierEvidenceSha256: string;
  typedBaselineEvidenceSha256: string | null;
  readbackId: string | null; readbackEvidenceSha256: string | null;
  reconciliationId: string | null; reconciliationEvidenceSha256: string | null;
  fullObjectClosureSha256: string | null;
  closureCheckpointId: string | null; closureCheckpointEvidenceSha256: string | null;
  canPush: false; canRetry: false; canDispatch: false; migrationComplete: false;
};
export type RemoteRecoveryWorkbenchPage = {
  preparationId: string; planId: string; repositoryId: number;
  items: RemoteRecoveryItem[]; nextAfter: string | null;
  canPush: false; canRetry: false; canDispatch: false; migrationComplete: false;
};
export type RemoteRecoveryActionResult = {
  state: string; evidenceSha256?: string; readbackId?: string;
  reconciliationId?: string; canRetry?: false; canPush?: false;
  migrationComplete: false;
};

export type Connection = {
  id: string; name: string; providerKey: string; direction: 'source' | 'destination';
  organization: string; createdAtUtc: string; validatedAtUtc: string | null;
  validationCode: string; authenticatedAs: string | null; permissionAssessment: string;
};
export type ConnectionDraft = { name: string; providerKey: string; organization: string; personalAccessToken: string };
export type ValidationResult = {
  isValid: boolean; code: string; authenticatedAs: string | null; permissionAssessment: string
};
export type AiProviderKey = 'OpenAICompatible' | 'AzureOpenAI';
export type AiAuthMode = 'ApiKey' | 'None' | 'ManagedIdentity';
export type AiConnection = {
  id: string; name: string; provider: AiProviderKey; endpoint: string; model: string; auth: AiAuthMode;
  apiVersion: string | null; tokenParameter: 'MaxTokens' | 'MaxCompletionTokens'; maxOutputTokens: number;
  temperature: number | null; timeoutSeconds: number; maxInputBytes: number; createdAtUtc: string;
  verifiedAtUtc: string | null; verificationCode: string; verifiedModel: string | null;
  sendSourceContent: boolean; consentAtUtc: string | null; consentBy: string | null;
};
export type AiConnectionDraft = {
  name: string; provider: AiProviderKey; endpoint: string; model: string; auth: AiAuthMode;
  apiKey?: string; apiVersion?: string; tokenParameter?: string; maxOutputTokens?: number;
  sendTemperature?: boolean; temperature?: number; timeoutSeconds?: number;
};
export type AiOptions = { providers: AiProviderKey[]; allowedHosts: string[]; allowHttp: boolean; managedIdentityAvailable: boolean };
export type AiVerification = { isValid: boolean; code: string; model: string | null; detail: string | null };
export type DiscoveryRefManifest = {
  jobId: string; sourceConnectionId: string;
  assetCount: number; chunkSize: number; chunkCount: number;
  rootSha256: string; preparedAtUtc: string;
};
export type DiscoveryRefManifestChunk = {
  index: number; assetCount: number; firstSourceId: string; lastSourceId: string; sha256: string;
};
export type DiscoveryRefChunkList = {
  jobId: string; items: DiscoveryRefManifestChunk[]; nextAfter: number | null;
};
export type DiscoveryRefChunkContents = {
  jobId: string; chunk: DiscoveryRefManifestChunk;
  items: { sourceId: string; repositoryId: string; fingerprint: string; properties: { ObjectId: string } }[];
};
export type DiscoveryGroupProgress = {
  jobId: string; checkpointedGroupPages: number; observedGroups: number;
};
export type DiscoveryGroupObservationPage = {
  jobId: string;
  items: { sourceId: string; fingerprint: string;
    properties: { DisplayName: string; LegacyDescriptor: string | null } }[];
  nextAfter: string | null;
};
export type DiscoveryAclProgress = {
  jobId: string; checkpointedAclPages: number; observedAces: number;
};
export type DiscoveryAclObservationPage = {
  jobId: string;
  items: { sourceId: string; securityToken: string; securityDescriptor: string;
    fingerprint: string; properties: { AllowMask: number; DenyMask: number; InheritPermissions: boolean } }[];
  nextAfter: string | null;
};
export type DiscoveryMembershipProgress = {
  jobId: string; checkpointedMembershipPages: number; observedMemberships: number;
};
export type DiscoveryMembershipObservationPage = {
  jobId: string;
  items: { sourceId: string; groupDescriptor: string;
    memberDescriptor: string; fingerprint: string }[];
  nextAfter: string | null;
};
export type DiscoveryIdentityProgress = {
  jobId: string; checkpointedUserPages: number; observedUsers: number;
};
export type DiscoveryIdentityObservationPage = {
  jobId: string;
  items: { sourceId: string; fingerprint: string;
    properties: { DisplayName: string; Email: string | null; LegacyDescriptor: string | null } }[];
  nextAfter: string | null;
};
export type DiscoveryRefIngestProgress = {
  jobId: string; checkpointedRefPages: number; observedRefs: number;
};
export type MigrationJob = {
  id: string; kind: 'StageSnapshot' | 'DiscoverSource'; snapshotId: string; sourceConnectionId: string;
  enqueuedBy: string; state: 'Queued' | 'Running' | 'Succeeded' | 'Failed';
  attempts: number; maxAttempts: number; availableAtUtc: string;
  leaseUntilUtc: string | null; createdAtUtc: string; updatedAtUtc: string;
  completedAtUtc: string | null; lastErrorCode: string | null;
  result: StagingSyncResult | null;
};
export type MigrationJobEvent = {
  id: number; jobId: string; eventType: string; attempt: number;
  occurredAtUtc: string; errorCode: string | null;
};
export type StagingSyncResult = {
  snapshotId: string; sourceConnectionId: string; sourceCollectedAtUtc: string;
  appliedAtUtc: string; observed: number; added: number; changed: number;
  unchanged: number; markedMissing: number; sourceInventoryComplete: boolean;
};
export type IncrementalOperationPlan = {
  id: string; preparationId: string; snapshotId: string;
  sourceConnectionId: string; destinationConnectionId: string;
  organization: string; reviewSha256: string;
  preparationEvidenceSha256: string; preflightEvidenceSha256: string;
  manifestSha256: string; createdAtUtc: string; createdBy: string;
  repositoryCount: number; stepCount: number; changedRefCount: number;
  sensitiveChangeCount: number; isCurrent: boolean; readyForExecution: boolean;
  blockers: string[];
  steps: { ordinal: number; dependsOnOrdinal: number | null;
    sourceRepositoryId: string; targetRepositoryName: string;
    kind: string; state: 'Blocked'; blockerCode: string;
    changedRefs: number }[];
};
// F-031/F-032: immutable, non-executable evidence from the real ASP.NET API.
export type GitHubTargetRefEvidence = {
  id: string; planId: string; sourceRepositoryId: string;
  targetRepositoryName: string; organization: string;
  manifestSha256: string; evidenceSha256: string;
  observedAtUtc: string; refCount: number; isCurrent: boolean;
  readyForExecution: false;
};
export type GitHubTargetRefEvidencePage = {
  evidence: GitHubTargetRefEvidence;
  items: { name: string; objectId: string; objectType: 'commit' | 'tag' }[];
  nextAfter: string | null;
};
export type GitRefDisposition = 0 | 1 | 2 | 3;
export type GitRefComparison = {
  name: string; sourceObjectId: string | null;
  targetObjectId: string | null; disposition: GitRefDisposition;
  blockerCode: string;
};
export type GitRefReconciliation = {
  id: string; planId: string; sourceRepositoryId: string;
  targetRepositoryName: string; targetObservationId: string;
  snapshotId: string; manifestSha256: string; sourceEvidenceSha256: string;
  sourceManifestRootSha256: string | null; targetEvidenceSha256: string;
  reconciliationSha256: string; createdAtUtc: string;
  sourceRefCount: number; targetRefCount: number; matchingCount: number;
  createCandidateCount: number; divergedCount: number; targetOnlyCount: number;
  isCurrent: boolean; readyForExecution: false;
};
export type GitRefReconciliationPage = {
  reconciliation: GitRefReconciliation;
  items: GitRefComparison[];
  nextAfter: string | null;
};
export type IncrementalTargetPreflight = {
  preparationId: string; snapshotId: string; destinationConnectionId: string;
  organization: string; reviewSha256: string; evidenceSha256: string;
  checkedAtUtc: string; revision: number; state: 'Blocked' | 'Unverified';
  membershipStatus: string; membershipRole: string; privateRepositoryPolicy: string;
  checkedRepositories: number; visibleCollisions: number; uncertainRepositories: number;
  membershipVerified: boolean; repositoryCreatePermissionVerified: boolean;
  targetAvailabilityVerified: boolean; readyForExecution: boolean;
  isCurrent: boolean;
  repositories: { sourceRepositoryId: string; targetRepositoryName: string;
    historyStrategy: string; status: string }[];
};
export type IncrementalReview = {
  preparationId: string; snapshotId: string; sourceConnectionId: string;
  destinationConnectionId: string;
  state: 'DryRunReady' | 'ApprovedForPlanning' | 'Rejected' | 'Revoked';
  evidenceSha256: string; dryRunSha256: string;
  itemCount: number; sensitiveItemCount: number; removalItemCount: number;
  dryRunAtUtc: string; reviewerId: string | null; reviewedAtUtc: string | null;
  reasonCode: string | null; sensitiveAcknowledged: boolean; isCurrent: boolean;
};
export type IncrementalReviewEvent = {
  id: number; preparationId: string; eventType: string; actorId: string;
  occurredAtUtc: string; dryRunSha256: string; reasonCode: string | null;
};
export type IncrementalPreparation = {
  id: string; snapshotId: string; sourceConnectionId: string;
  destinationConnectionId: string; enqueuedBy: string;
  state: 'Queued' | 'Running' | 'Prepared' | 'Failed';
  attempts: number; maxAttempts: number; availableAtUtc: string;
  leaseUntilUtc: string | null; createdAtUtc: string;
  updatedAtUtc: string; preparedAtUtc: string | null;
  lastErrorCode: string | null; itemCount: number;
  sensitiveItemCount: number; evidenceSha256: string | null;
};
export type IncrementalPreparationItemPage = {
  preparationId: string;
  items: {
    assetType: string; sourceId: string; changeKind: string;
    previousFingerprint: string | null; currentFingerprint: string | null;
    revision: number; reviewCategory: string; reviewState: 'NeedsReview';
  }[];
  nextAfterType: string | null; nextAfterId: string | null;
};
export type StagingChangeSummary = {
  snapshotId: string; sourceConnectionId: string;
  added: number; changed: number; restored: number; missing: number; total: number;
};
export type StagingChangePage = {
  snapshotId: string; assetType: string;
  items: {
    assetType: string; sourceId: string; kind: 'Added' | 'Changed' | 'Restored' | 'Missing';
    previousFingerprint: string | null; currentFingerprint: string | null;
    revision: number;
  }[];
  nextAfter: string | null;
};
export type StagingCatalogSummary = {
  sourceConnectionId: string; latestSnapshotId: string | null; lastSyncAtUtc: string | null;
  activeAssets: number; missingAssets: number; totalRevisions: number;
};
export type DiscoverySummary = {
  id: string; connectionId: string; sourceProvider: string; organization: string; collectedAtUtc: string;
  completeness: 'complete' | 'partial'; projectCount: number; repositoryCount: number;
  refCount: number; refsExternalized?: boolean; identityCount: number;
  identitiesExternalized?: boolean; issueCount: number;
  groupCount?: number; groupsExternalized?: boolean;
  membershipCount?: number; membershipsExternalized?: boolean;
  aclEntryCount?: number; aclsExternalized?: boolean;
  securityCoverage?: SecurityCoverage;
  identitiesComplete: boolean; permissionsComplete: boolean;
};
export type DiscoveredRepository = {
  providerId: string; projectId: string; name: string; versionControl: number; sourcePath: string | null
};
export type SecurityCoverage = {
  groupsComplete: boolean; membershipsComplete: boolean; aclsCollected: boolean;
  effectivePermissionsVerified: boolean;
};
export type SecurityGroup = { descriptor: string; displayName: string; legacyDescriptor: string | null };
export type SecurityMembership = { groupDescriptor: string; memberDescriptor: string };
export type AclEvidence = {
  repositoryId: string; securityToken: string; securityDescriptor: string;
  allowMask: number; denyMask: number; inheritPermissions: boolean; scope: 'project' | 'repository';
};
export type DiscoveryDetail = {
  id: string; connectionId: string; organization: string; collectedAtUtc: string;
  snapshot: {
    projects: { providerId: string; name: string; repositories: DiscoveredRepository[] }[];
    identities: { providerId: string; displayName: string; email: string | null; legacyDescriptor?: string | null }[];
  };
  refs: { repositoryId: string; name: string; objectId: string }[];
  refsExternalized?: boolean; refCount?: number;
  identitiesExternalized?: boolean; identityCount?: number;
  issues: { code: string; projectId: string | null; repositoryId: string | null }[];
  groups?: SecurityGroup[]; groupsExternalized?: boolean; groupCount?: number;
  memberships?: SecurityMembership[];
  membershipsExternalized?: boolean; membershipCount?: number;
  aclEvidence?: AclEvidence[]; aclsExternalized?: boolean; aclCount?: number;
  securityCoverage?: SecurityCoverage;
  identitiesComplete: boolean; permissionsComplete: boolean;
};
export type IdentityMap = { sourceIdentityId: string; gitHubLogin: string };
export type IdentityDraft = {
  snapshotId: string; destinationConnectionId: string; targetOrganization: string;
  revision: number; updatedAtUtc: string; mappings: IdentityMap[];
};
export type IdentityPreviewIssue = { code: string; sourceIdentityId: string | null };
export type IdentityPreview = {
  discoveredIdentities: number; mappedIdentities: number; unmappedIdentities: number;
  unresolvedAclPrincipals: number; sourceIdentitiesComplete: boolean;
  effectivePermissionsVerified: boolean; targetAccountsVerified: boolean;
  readyForGrants: boolean; issues: IdentityPreviewIssue[];
};
export type IdentityReview = { draft: IdentityDraft; preview: IdentityPreview };
export type IdentitySourceItem = {
  sourceIdentityId: string; displayName: string; email: string | null;
  legacyDescriptor: string | null;
};
export type IdentitySourcePage = {
  snapshotId: string; items: IdentitySourceItem[]; nextAfter: string | null;
};

export type PlanSelection = {
  sourceRepositoryId: string; targetRepositoryName: string; strategy: number; include: boolean;
};
export type TargetRepositoryProbeResult = {
  sourceRepositoryId: string; targetRepositoryName: string;
  status: 'Exists' | 'NotVisibleOrMissing' | 'Unauthorized' | 'Forbidden' |
    'RateLimited' | 'Unavailable' | 'InvalidResponse';
};
export type DestinationAccessReview = {
  destinationConnectionId: string; targetOrganization: string; authenticatedAs: string;
  checkedAtUtc: string; membershipStatus: string; membershipRole: string;
  privateRepositoryPolicy: string; membershipVerified: boolean;
  repositoryCreatePermissionVerified: boolean; readyForApproval: boolean;
};
export type MigrationTargetPreflight = {
  snapshotId: string; destinationConnectionId: string; targetOrganization: string;
  checkedAtUtc: string; checkedCount: number;
  targetAvailabilityVerified: boolean; readyForApproval: boolean;
  repositories: TargetRepositoryProbeResult[];
};
export type MigrationPlanDraft = {
  snapshotId: string; destinationConnectionId: string; targetOrganization: string;
  revision: number; updatedAtUtc: string; repositories: PlanSelection[];
};
export type PlanCheck = { code: string; severity: 'blocker' | 'warning'; repositoryId: string | null };
export type PlanRepositoryReview = {
  sourceRepositoryId: string; sourceProjectId: string; sourceName: string;
  targetRepositoryName: string; versionControl: number; strategy: number;
  refCount: number; candidateGitHubLogins: string[];
};
export type PlanReview = {
  snapshotId: string; destinationConnectionId: string; targetOrganization: string;
  identityDraftRevision: number; selectedRepositories: number; availableRepositories: number;
  effectivePermissionsVerified: boolean; targetRepositoryAvailabilityVerified: boolean;
  targetMembershipVerified: boolean; readyForApproval: boolean;
  repositories: PlanRepositoryReview[]; checks: PlanCheck[];
};

export type WorkloadIssue = { code: string; detail: string; severity: string };
export type SetupStep = { name: string; kind: string; existsOnGitHub: boolean; instructions: string };
export type PipelineBlueprint = {
  pipelineId: number; sourceName: string; targetRepository: string; workflowPath: string;
  workflowYaml: string; setupSteps: SetupStep[]; issues: WorkloadIssue[];
  ai: { status: string; findings: string[] }; deployableWithoutReview: boolean;
};
export type ConversionGate = { name: string; status: 'Passed' | 'Failed' | 'NotRun'; detail: string | null };
export type ConversionFinding = { gate: string; code: string; severity: 'Info' | 'Warning' | 'Blocker'; message: string; line: number | null };
export type ConversionAttempt = {
  number: number; outcome: string; promptTokens: number | null; completionTokens: number | null;
  workflowSha256: string | null; blockerCodes: string[];
};
export type ConversionProposal = {
  status: 'ReadyForReview' | 'Blocked' | 'NotAttempted'; reason: string | null; workflowYaml: string | null;
  workflowSha256: string | null; modelNotes: string | null; approvable: boolean; proposalOnly: boolean; saved: boolean;
  proposalId?: string | null; proposalState?: ProposalState | null;
  gates: ConversionGate[]; findings: ConversionFinding[];
  plan: {
    secrets: { name: string; origin: string }[]; variables: { name: string; origin: string }[];
    connections: { connection: string; kind: string; guidance: string; secrets: string[]; variables: string[] }[]; notes: string[];
  };
  provenance: {
    aiConnectionId: string; provider: string; host: string; model: string; reportedModel: string | null;
    promptSha256: string; planSha256: string; policyVersion: string; actionCatalogVerifiedOn: string;
    redactions: { kind: string; count: number }[]; totalPromptTokens: number; totalCompletionTokens: number;
    attempts: ConversionAttempt[]; generatedAtUtc: string;
  } | null;
};
export type ProposalState = 'Proposed' | 'Blocked' | 'Approved' | 'Rejected' | 'Revoked';
export type ProposalSummary = {
  id: string; state: ProposalState; reason: string | null; workflowSha256: string | null; createdBy: string; createdAtUtc: string;
};
export type ProposalEvent = {
  id: number; type: 'Created' | 'Approved' | 'Rejected' | 'Revoked'; actor: string; occurredAtUtc: string;
  workflowSha256: string | null; note: string | null; reportSha256: string | null; policyVersion: string | null; acknowledgedWarnings: string[];
};
export type ProposalDetail = Pick<ConversionProposal, 'gates' | 'findings' | 'plan' | 'provenance' | 'workflowYaml' | 'workflowSha256' | 'modelNotes' | 'reason'> & {
  id: string; state: ProposalState; sourceConnectionId: string; projectId: string; pipelineId: number; aiConnectionId: string;
  approvedSha256: string | null; integrityOk: boolean; createdBy: string; createdAtUtc: string; createdByMe: boolean;
  requiresSecondApprover: boolean; canApprove: boolean; events: ProposalEvent[];
};
export type ConversionStatus = {
  actionlint: boolean; zizmor: boolean; canBeApprovable: boolean; policyVersion: string; actionCatalogVerifiedOn: string;
};
export type BoardIssueDraft = {
  sourceId: number; title: string; body: string; state: string; issues: WorkloadIssue[];
};
export type BoardsPreview = { issues: BoardIssueDraft[]; globalWarnings: WorkloadIssue[]; readyToPublish: boolean };
export type BoardLinkCheck = {
  workItemId: number; sourceRelation: string; sourceReference: string;
  targetReference: string | null;
  status: 'targetExists' | 'missingMapping' | 'notFound' | 'unavailable' | 'unsupported' | 'notChecked';
};
export type BoardReconciliationReport = {
  targetOrganization: string; targetRepository: string; links: BoardLinkCheck[];
  targetObjectsFound: number; unresolvedLinks: number;
  issues: WorkloadIssue[]; readyToPublish: boolean;
};
const base = import.meta.env.VITE_API_URL ?? '';

// The message stays the machine code the API answered with; `details` carries the codes that explain it (never text from a workflow).
export class ApiError extends Error {
  constructor(message: string, public readonly details: string[] = [], public readonly status = 0) { super(message); }
}

async function call<T>(path: string, options: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(base + path, { ...options, headers: { 'Content-Type': 'application/json', ...options.headers } });
  } catch {
    throw new Error('Network error - verify that the API is running.');
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string; error?: string; details?: string[] } | null;
    throw new ApiError(problem?.error ?? problem?.title ?? `HTTP ${response.status}`,
      Array.isArray(problem?.details) ? problem.details : [], response.status);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

const withToken = (token: string) => ({ Authorization: `Bearer ${token}` });
export const api = {
  login: (username: string, password: string) =>
    call<Session>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  changePassword: (token: string, currentPassword: string, newPassword: string) =>
    call<Session>('/api/auth/change-password', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ currentPassword, newPassword })
    }),
  createUser: (token: string, username: string, temporaryPassword: string, role: string) =>
    call<{ id: string }>('/api/users', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ username, temporaryPassword, role })
    }),
  listConnections: (token: string) =>
    call<Connection[]>('/api/connections', { headers: withToken(token) }),
  validateConnection: (token: string, draft: ConnectionDraft) =>
    call<ValidationResult>('/api/connections/validate', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  saveConnection: (token: string, draft: ConnectionDraft) =>
    call<Connection>('/api/connections', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  verifyConnection: (token: string, id: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/verify`, {
      method: 'POST', headers: withToken(token)
    }),
  rotateCredential: (token: string, id: string, personalAccessToken: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/credential`, {
      method: 'PUT', headers: withToken(token), body: JSON.stringify({ personalAccessToken })
    }),
  aiOptions: (token: string) =>
    call<AiOptions>('/api/ai-connections/options', { headers: withToken(token) }),
  listAiConnections: (token: string) =>
    call<AiConnection[]>('/api/ai-connections', { headers: withToken(token) }),
  validateAiConnection: (token: string, draft: AiConnectionDraft) =>
    call<AiVerification>('/api/ai-connections/validate', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  saveAiConnection: (token: string, draft: AiConnectionDraft) =>
    call<AiConnection>('/api/ai-connections', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  verifyAiConnection: (token: string, id: string) =>
    call<AiConnection>(`/api/ai-connections/${encodeURIComponent(id)}/verify`, { method: 'POST', headers: withToken(token) }),
  rotateAiCredential: (token: string, id: string, apiKey: string) =>
    call<AiConnection>(`/api/ai-connections/${encodeURIComponent(id)}/credential`, {
      method: 'PUT', headers: withToken(token), body: JSON.stringify({ apiKey })
    }),
  setAiSourceConsent: (token: string, id: string, sendSourceContent: boolean) =>
    call<AiConnection>(`/api/ai-connections/${encodeURIComponent(id)}/source-consent`, {
      method: 'PUT', headers: withToken(token), body: JSON.stringify({ sendSourceContent })
    }),
  deleteAiConnection: (token: string, id: string) =>
    call<void>(`/api/ai-connections/${encodeURIComponent(id)}`, { method: 'DELETE', headers: withToken(token) }),
  listDiscoveries: (token: string) =>
    call<DiscoverySummary[]>('/api/discoveries', { headers: withToken(token) }),
  getDiscovery: (token: string, id: string) =>
    call<DiscoveryDetail>(`/api/discoveries/${encodeURIComponent(id)}`, { headers: withToken(token) }),
  scanDiscovery: (token: string, connectionId: string) =>
    call<DiscoverySummary>('/api/discoveries', {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ connectionId })
    }),
  enqueueDiscoveryJob: (token: string, connectionId: string, requestId: string) =>
    call<MigrationJob>('/api/jobs/discovery', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ connectionId, requestId })
    }),
  enqueueStagingJob: (token: string, snapshotId: string) =>
    call<MigrationJob>('/api/jobs/staging', {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ snapshotId })
    }),
  getDiscoveryRefManifest: (token: string, jobId: string) =>
    call<DiscoveryRefManifest>(`/api/jobs/${encodeURIComponent(jobId)}/ref-manifest`,
      { headers: withToken(token) }),
  listDiscoveryRefChunks: (token: string, jobId: string, after = -1, take = 20) =>
    call<DiscoveryRefChunkList>(`/api/jobs/${encodeURIComponent(jobId)}/ref-manifest/chunks?after=${after}&take=${take}`,
      { headers: withToken(token) }),
  getDiscoveryRefChunk: (token: string, jobId: string, index: number) =>
    call<DiscoveryRefChunkContents>(`/api/jobs/${encodeURIComponent(jobId)}/ref-manifest/chunks/${index}`,
      { headers: withToken(token) }),
  getDiscoveryGroupProgress: (token: string, jobId: string) =>
    call<DiscoveryGroupProgress>(`/api/jobs/${encodeURIComponent(jobId)}/group-progress`,
      { headers: withToken(token) }),
  listDiscoveryGroupObservations: (token: string, jobId: string, after?: string, take = 50) =>
    call<DiscoveryGroupObservationPage>(
      `/api/jobs/${encodeURIComponent(jobId)}/groups?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  getDiscoveryAclProgress: (token: string, jobId: string) =>
    call<DiscoveryAclProgress>(`/api/jobs/${encodeURIComponent(jobId)}/acl-progress`,
      { headers: withToken(token) }),
  listDiscoveryAclObservations: (token: string, jobId: string, after?: string, take = 50) =>
    call<DiscoveryAclObservationPage>(
      `/api/jobs/${encodeURIComponent(jobId)}/acls?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  getDiscoveryMembershipProgress: (token: string, jobId: string) =>
    call<DiscoveryMembershipProgress>(`/api/jobs/${encodeURIComponent(jobId)}/membership-progress`,
      { headers: withToken(token) }),
  listDiscoveryMembershipObservations: (token: string, jobId: string, after?: string, take = 50) =>
    call<DiscoveryMembershipObservationPage>(
      `/api/jobs/${encodeURIComponent(jobId)}/memberships?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  getDiscoveryIdentityProgress: (token: string, jobId: string) =>
    call<DiscoveryIdentityProgress>(`/api/jobs/${encodeURIComponent(jobId)}/identity-progress`,
      { headers: withToken(token) }),
  listDiscoveryIdentityObservations: (token: string, jobId: string, after?: string, take = 50) =>
    call<DiscoveryIdentityObservationPage>(
      `/api/jobs/${encodeURIComponent(jobId)}/identities?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  getDiscoveryRefProgress: (token: string, jobId: string) =>
    call<DiscoveryRefIngestProgress>(`/api/jobs/${encodeURIComponent(jobId)}/ref-progress`,
      { headers: withToken(token) }),
  getMigrationJob: (token: string, jobId: string) =>
    call<MigrationJob>(`/api/jobs/${encodeURIComponent(jobId)}`,
      { headers: withToken(token) }),
  listMigrationJobs: (token: string, sourceConnectionId: string) =>
    call<MigrationJob[]>(`/api/jobs/connections/${encodeURIComponent(sourceConnectionId)}`,
      { headers: withToken(token) }),
  getMigrationJobEvents: (token: string, jobId: string) =>
    call<MigrationJobEvent[]>(`/api/jobs/${encodeURIComponent(jobId)}/events`,
      { headers: withToken(token) }),
  syncStagingSnapshot: (token: string, snapshotId: string) =>
    call<StagingSyncResult>(`/api/staging/snapshots/${encodeURIComponent(snapshotId)}/sync`, {
      method: 'POST', headers: withToken(token)
    }),
  getStagingSync: (token: string, snapshotId: string) =>
    call<StagingSyncResult>(`/api/staging/snapshots/${encodeURIComponent(snapshotId)}`,
      { headers: withToken(token) }),
  createIncrementalOperationPlan: (token: string, id: string,
      expectedPreflightSha256: string) =>
    call<IncrementalOperationPlan>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/operation-plans`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ expectedPreflightSha256 }) }),
  listIncrementalOperationPlans: (token: string, id: string) =>
    call<IncrementalOperationPlan[]>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/operation-plans`,
      { headers: withToken(token) }),
  getMultiRepositorySyncPulse: (token: string, preparationId: string,
      planId: string) =>
    call<MultiRepositorySyncPulse>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/repository-sync-pulse`,
      { headers: withToken(token) }),
  listSourceSyncCheckpoints: (token: string,
      preparationId: string, planId: string,
      after?: string | null, take = 10) =>
    call<SourceSyncCheckpointPage>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  listSourceCheckpointRefChanges: (token: string,
      preparationId: string, planId: string, checkpointId: string,
      after?: string | null, take = 50) =>
    call<SourceCheckpointRefChangePage>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/ref-changes?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  reviewCheckpointTargetRefNames: (
      token: string, preparationId: string, planId: string,
      checkpointId: string, targetObservationId: string,
      sourceRepositoryId: string, expectedTargetEvidenceSha256: string,
      after?: string | null, take = 50) =>
    call<CheckpointTargetRefReviewPage>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/target-ref-review/${encodeURIComponent(targetObservationId)}?sourceRepositoryId=${encodeURIComponent(sourceRepositoryId)}&expectedTargetEvidenceSha256=${encodeURIComponent(expectedTargetEvidenceSha256)}&take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  readCheckpointHumanReview: (token: string,
      preparationId: string, planId: string, checkpointId: string,
      sourceRepositoryId: string) =>
    call<CheckpointHumanReviewReceipt>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/target-human-review?sourceRepositoryId=${encodeURIComponent(sourceRepositoryId)}`,
      { headers: withToken(token) }),
  previewCheckpointHumanReview: (token: string,
      preparationId: string, planId: string, checkpointId: string,
      sourceRepositoryId: string) =>
    call<CheckpointHumanReviewCandidate>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/target-human-review/candidate?sourceRepositoryId=${encodeURIComponent(sourceRepositoryId)}`,
      { headers: withToken(token) }),
  readCheckpointTargetObservation: (token: string,
      preparationId: string, planId: string, checkpointId: string,
      sourceRepositoryId: string) =>
    call<CheckpointTargetObservation>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/target-ref-observations?sourceRepositoryId=${encodeURIComponent(sourceRepositoryId)}`,
      { headers: withToken(token) }),
  readLaterCheckpointWorkpackHistory: (token: string,
      preparationId: string, planId: string, checkpointId: string,
      workpackId: string, sourceRepositoryId: string,
      targetObservationId: string, expectedTargetReceiptSha256: string) =>
    call<LaterCheckpointWorkpackHistory>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(checkpointId)}/later-workpacks/${encodeURIComponent(workpackId)}/history?sourceRepositoryId=${encodeURIComponent(sourceRepositoryId)}&targetObservationId=${encodeURIComponent(targetObservationId)}&expectedTargetReceiptSha256=${encodeURIComponent(expectedTargetReceiptSha256)}`,
      { headers: withToken(token) }),
  captureSourceSyncCheckpoint: (token: string,
      preparationId: string, planId: string, snapshotId: string,
      expectedManifestSha256: string,
      expectedPreviousCheckpointId: string | null) =>
    call<SourceSyncCheckpointReceipt>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/source-sync-checkpoints/${encodeURIComponent(snapshotId)}`, {
        method: 'POST', headers: withToken(token),
        body: JSON.stringify({
          expectedManifestSha256, expectedPreviousCheckpointId
        })
      }),
  listRemoteRecoveryWorkbench: (token: string, preparationId: string,
      planId: string, after?: string | null, take = 12) =>
    call<RemoteRecoveryWorkbenchPage>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/remote-recovery-workbench?take=${take}` +
        (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  executeIndependentRecoveryAction: (token: string, epoch: RemoteRecoveryItem,
      action: 'observe-tips' | 'verify-full-objects' | 'closure-checkpoints') => {
    const root = `/api/incremental-preparations/${encodeURIComponent(epoch.preparationId)}/operation-plans/${encodeURIComponent(epoch.planId)}/github-app-scopes/${encodeURIComponent(epoch.scopeId)}/remote-sandbox-review-sessions/${encodeURIComponent(epoch.sessionId)}/dispatch-intents/${encodeURIComponent(epoch.intentId)}/confirmations/${encodeURIComponent(epoch.confirmationId)}`;
    const path = action === 'closure-checkpoints'
      ? `${root}/post-send-recovery/${encodeURIComponent(epoch.barrierId)}/closure-checkpoints`
      : `${root}/independent-recovery/${encodeURIComponent(epoch.barrierId)}/${action}`;
    return call<RemoteRecoveryActionResult>(path, {
      method: 'POST', headers: withToken(token)
    });
  },
  getIncrementalOperationPlan: (token: string, id: string, planId: string) =>
    call<IncrementalOperationPlan>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/operation-plans/${encodeURIComponent(planId)}`,
      { headers: withToken(token) }),
  listGitHubTargetRefEvidence: (token: string, preparationId: string, planId: string) =>
    call<GitHubTargetRefEvidence[]>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/target-ref-evidence`,
      { headers: withToken(token) }),
  captureGitHubTargetRefEvidence: (token: string, preparationId: string,
      planId: string, expectedManifestSha256: string, sourceRepositoryId: string) =>
    call<GitHubTargetRefEvidence>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/target-ref-evidence`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ expectedManifestSha256, sourceRepositoryId }) }),
  listGitRefReconciliations: (token: string, preparationId: string, planId: string) =>
    call<GitRefReconciliation[]>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/ref-reconciliations`,
      { headers: withToken(token) }),
  createGitRefReconciliation: (token: string, preparationId: string, planId: string,
      sourceRepositoryId: string, targetObservationId: string, expectedManifestSha256: string,
      expectedTargetEvidenceSha256: string) =>
    call<GitRefReconciliation>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/ref-reconciliations`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ sourceRepositoryId, targetObservationId,
          expectedManifestSha256, expectedTargetEvidenceSha256 }) }),
  getGitRefReconciliationPage: (token: string, preparationId: string,
      planId: string, reconciliationId: string, after?: string | null, take = 50) =>
    call<GitRefReconciliationPage>(
      `/api/incremental-preparations/${encodeURIComponent(preparationId)}/operation-plans/${encodeURIComponent(planId)}/ref-reconciliations/${encodeURIComponent(reconciliationId)}?take=${take}` +
      (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  runLiveTargetPreflight: (token: string, id: string, expectedReviewSha256: string,
      repositories: { sourceRepositoryId: string; targetRepositoryName: string;
        strategy: number; include: boolean }[]) =>
    call<IncrementalTargetPreflight>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/target-preflight`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ expectedReviewSha256, repositories }) }),
  getLiveTargetPreflight: (token: string, id: string) =>
    call<IncrementalTargetPreflight>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/target-preflight`,
      { headers: withToken(token) }),
  getIncrementalReview: (token: string, id: string) =>
    call<IncrementalReview>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/review`,
      { headers: withToken(token) }),
  runIncrementalDryRun: (token: string, id: string) =>
    call<IncrementalReview>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/dry-run`,
      { method: 'POST', headers: withToken(token), body: '{}' }),
  decideIncrementalReview: (token: string, id: string,
      decision: 'ApproveForPlanning' | 'Reject',
      expectedDryRunSha256: string, acknowledgeSensitive: boolean,
      reasonCode?: string) =>
    call<IncrementalReview>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/review/decision`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ decision, expectedDryRunSha256,
          acknowledgeSensitive, reasonCode: reasonCode || null }) }),
  revokeIncrementalReview: (token: string, id: string,
      expectedDryRunSha256: string, reasonCode: string) =>
    call<IncrementalReview>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/review/revoke`,
      { method: 'POST', headers: withToken(token),
        body: JSON.stringify({ expectedDryRunSha256, reasonCode }) }),
  listIncrementalReviewEvents: (token: string, id: string) =>
    call<IncrementalReviewEvent[]>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/review/events`,
      { headers: withToken(token) }),
  enqueueIncrementalPreparation: (token: string, snapshotId: string,
      destinationConnectionId: string) =>
    call<IncrementalPreparation>('/api/incremental-preparations', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ snapshotId, destinationConnectionId })
    }),
  getIncrementalPreparation: (token: string, id: string) =>
    call<IncrementalPreparation>(
      `/api/incremental-preparations/${encodeURIComponent(id)}`,
      { headers: withToken(token) }),
  listIncrementalPreparationItems: (token: string, id: string,
      afterType?: string | null, afterId?: string | null, take = 25) =>
    call<IncrementalPreparationItemPage>(
      `/api/incremental-preparations/${encodeURIComponent(id)}/items?take=${take}` +
      (afterType && afterId
        ? `&afterType=${encodeURIComponent(afterType)}&afterId=${encodeURIComponent(afterId)}`
        : ''),
      { headers: withToken(token) }),
  getStagingChangeSummary: (token: string, snapshotId: string) =>
    call<StagingChangeSummary>(
      `/api/staging/snapshots/${encodeURIComponent(snapshotId)}/change-summary`,
      { headers: withToken(token) }),
  listStagingChanges: (token: string, snapshotId: string, assetType: string,
      after?: string | null, take = 50) =>
    call<StagingChangePage>(
      `/api/staging/snapshots/${encodeURIComponent(snapshotId)}/changes/${encodeURIComponent(assetType)}?take=${take}` +
      (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  getStagingSummary: (token: string, connectionId: string) =>
    call<StagingCatalogSummary>(`/api/staging/connections/${encodeURIComponent(connectionId)}/summary`,
      { headers: withToken(token) }),
  getIdentityReview: (token: string, snapshotId: string, destinationConnectionId: string) =>
    call<IdentityReview>(`/api/identity-mappings/${encodeURIComponent(snapshotId)}?destinationConnectionId=${encodeURIComponent(destinationConnectionId)}`,
      { headers: withToken(token) }),
  listIdentitySources: (token: string, snapshotId: string,
      destinationConnectionId: string, after?: string | null, take = 100) =>
    call<IdentitySourcePage>(
      `/api/identity-mappings/${encodeURIComponent(snapshotId)}/sources` +
      `?destinationConnectionId=${encodeURIComponent(destinationConnectionId)}&take=${take}` +
      (after ? `&after=${encodeURIComponent(after)}` : ''),
      { headers: withToken(token) }),
  saveIdentityMappings: (token: string, snapshotId: string, destinationConnectionId: string,
      expectedRevision: number, mappings: IdentityMap[]) =>
    call<IdentityReview>(`/api/identity-mappings/${encodeURIComponent(snapshotId)}`,
      { method: 'PUT', headers: withToken(token),
        body: JSON.stringify({ destinationConnectionId, expectedRevision, mappings }) }),
  getMigrationPlanDraft: (token: string, snapshotId: string, destinationConnectionId: string) =>
    call<MigrationPlanDraft>(`/api/migration-plans/drafts/${encodeURIComponent(snapshotId)}?destinationConnectionId=${encodeURIComponent(destinationConnectionId)}`,
      { headers: withToken(token) }),
  saveMigrationPlanDraft: (token: string, snapshotId: string, destinationConnectionId: string,
      expectedRevision: number, repositories: PlanSelection[]) =>
    call<MigrationPlanDraft>(`/api/migration-plans/drafts/${encodeURIComponent(snapshotId)}`, {
      method: 'PUT', headers: withToken(token),
      body: JSON.stringify({ destinationConnectionId, expectedRevision, repositories })
    }),
  checkDestinationAccess: (token: string, destinationConnectionId: string) =>
    call<DestinationAccessReview>(
      `/api/migration-plans/destinations/${encodeURIComponent(destinationConnectionId)}/access`,
      { headers: withToken(token) }),
  preflightMigrationPlanTargets: (token: string, snapshotId: string, destinationConnectionId: string,
      repositories: PlanSelection[]) =>
    call<MigrationTargetPreflight>('/api/migration-plans/preflight', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ snapshotId, destinationConnectionId, repositories })
    }),
  previewMigrationPlan: (token: string, snapshotId: string, destinationConnectionId: string,
      repositories: PlanSelection[]) =>
    call<PlanReview>('/api/migration-plans/preview', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ snapshotId, destinationConnectionId, repositories })
    }),
  previewPipeline: (token: string, sourceConnectionId: string, destinationConnectionId: string,
    projectId: string, pipelineId: number, targetRepository: string) =>
    call<PipelineBlueprint>('/api/workloads/pipelines/preview', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ sourceConnectionId, destinationConnectionId, projectId, pipelineId, targetRepository })
    }),
  conversionStatus: (token: string) =>
    call<ConversionStatus>('/api/workloads/pipelines/convert/status', { headers: withToken(token) }),
  convertPipeline: (token: string, sourceConnectionId: string, projectId: string, pipelineId: number, aiConnectionId: string) =>
    call<ConversionProposal>('/api/workloads/pipelines/convert', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ sourceConnectionId, projectId, pipelineId, aiConnectionId })
    }),
  listProposals: (token: string, sourceConnectionId: string, projectId: string, pipelineId: number) =>
    call<ProposalSummary[]>('/api/workloads/pipelines/proposals?' + new URLSearchParams({
      sourceConnectionId, projectId, pipelineId: String(pipelineId) }), { headers: withToken(token) }),
  getProposal: (token: string, id: string) =>
    call<ProposalDetail>(`/api/workloads/pipelines/proposals/${id}`, { headers: withToken(token) }),
  approveProposal: (token: string, id: string, workflowSha256: string, acknowledgedWarnings: string[], note: string) =>
    call<ProposalDetail>(`/api/workloads/pipelines/proposals/${id}/approve`, {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ workflowSha256, acknowledgedWarnings, note })
    }),
  rejectProposal: (token: string, id: string, note: string) =>
    call<ProposalDetail>(`/api/workloads/pipelines/proposals/${id}/reject`, {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ note })
    }),
  revokeProposal: (token: string, id: string, note: string) =>
    call<ProposalDetail>(`/api/workloads/pipelines/proposals/${id}/revoke`, {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ note })
    }),
  previewBoards: (token: string, sourceConnectionId: string, projectId: string,
    workItemIds: number[], knownGitHubIssues: Record<number, number>) =>
    call<BoardsPreview>('/api/workloads/boards/preview', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ sourceConnectionId, projectId, workItemIds, knownGitHubIssues })
    }),
  reconcileBoards: (token: string, sourceConnectionId: string, destinationConnectionId: string,
    projectId: string, targetRepository: string, workItemIds: number[],
    knownGitHubIssues: Record<number, number>, knownGitHubCommits: Record<string, string>,
    knownGitHubPullRequests: Record<number, number>) =>
    call<BoardReconciliationReport>('/api/workloads/boards/reconcile', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ sourceConnectionId, destinationConnectionId, projectId, targetRepository,
        workItemIds, knownGitHubIssues, knownGitHubCommits, knownGitHubPullRequests })
    }),
  deleteConnection: (token: string, id: string) =>
    call<void>(`/api/connections/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: withToken(token)
    })
};
