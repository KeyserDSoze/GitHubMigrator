using System.Runtime.InteropServices;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// #123: OFF by default and deliberately NOT registered in API DI.
// This is only a local physical primitive. A separate future
// source-locked PostgreSQL coordinator MUST authenticate/recheck the
// approved #122 decision, record the rename and fence any new F-061
// generation. The caller can NEVER make this receipt authoritative.
public sealed class LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
    string bundleRoot, string mirrorRoot,
    bool enabled = false, TimeProvider? clock = null)
    : IGitIncrementalLaterCheckpointMirrorPhysicalIsolationSession
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Directory.Move checks the destination separately from rename(2).
    // On POSIX, an outsider can create an empty directory between those
    // operations, making rename(2) replace it. The importer lock only
    // coordinates OUR processes, not arbitrary other filesystem writers.
    // Linux renameat2(RENAME_NOREPLACE) performs the collision check in
    // the kernel atomically; unsupported filesystems/OSes fail closed.
    private const int AtCurrentDirectory = -100; // AT_FDCWD
    private const uint NoReplace = 1; // RENAME_NOREPLACE

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int RenameNoReplace(
        int oldDirectoryFd, string oldPath,
        int newDirectoryFd, string newPath, uint flags);

    private static void MoveDirectoryWithoutReplacement(
        string original, string isolated)
    {
        if (!OperatingSystem.IsLinux())
            throw new IOException(
                "F-061v2 orphan isolation requires atomic no-replace rename");
        try
        {
            if (RenameNoReplace(AtCurrentDirectory, original,
                AtCurrentDirectory, isolated, NoReplace) != 0)
                throw new IOException(
                    "F-061v2 orphan isolation no-replace rename refused");
        }
        catch (EntryPointNotFoundException ex)
        {
            // Older libc/kernel combinations may not expose renameat2.
            // Never fall back to Directory.Move/replace semantics.
            throw new IOException(
                "F-061v2 atomic no-replace rename is unavailable", ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new IOException(
                "F-061v2 POSIX rename library is unavailable", ex);
        }
    }

    // #124: read-only classification of a prepared operation after a
    // process restart. No SQL authority is inferred from this local
    // result: the DB coordinator must separately verify the ACTUAL
    // source-locked Prepared row and its immutable event both before
    // and after filesystem work. This method never renames/deletes.
    public const string PreparedOriginalIntact = "PreparedOriginalIntact";
    public const string PreparedIsolatedIntact = "PreparedIsolatedIntact";
    public const string PreparedBothPresent = "PreparedBothPresent";
    public const string PreparedBothMissing = "PreparedBothMissing";
    public const string PreparedOriginalCorrupt = "PreparedOriginalCorrupt";
    public const string PreparedIsolatedCorrupt = "PreparedIsolatedCorrupt";
    public const string PreparedUnsafePath = "PreparedUnsafePath";

    public async Task<string> InspectPreparedPathsAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
        CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .VerifyForPhysicalIsolation(decision, inspection);
        // The original inspection was made of UUID.git. Its stable
        // candidate and the signed intent still have to agree after
        // the move, even though a subsequent original-path audit
        // cannot succeed once UUID.git has been renamed.
        GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
            .Verify(intent, inspection, decision);
        if (stage.PhysicalMirrorId != inspection.PhysicalMirrorId ||
            stage.SourceSnapshotId != inspection.SourceSnapshotId ||
            stage.WorkpackId != inspection.WorkpackId ||
            stage.CheckpointId != inspection.CheckpointId ||
            stage.EvidenceSha256 != inspection.StagingEvidenceSha256 ||
            durable.BundleSha256 != inspection.BundleSha256 ||
            durable.EvidenceSha256 != inspection.BundleEvidenceSha256 ||
            stage.SourceFullRefProofSha256 != inspection.FullRefProofSha256 ||
            stage.SourceRefSetSha256 != inspection.SourceRefSetSha256)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");

        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var store = new LocalGitBundleArtifactStore(bundleRoot);
        if (string.Equals(root, store.Root, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        var original = Path.Combine(root,
            stage.PhysicalMirrorId.ToString("N") + ".git");
        var isolated = Path.Combine(root, intent.IsolationDirectoryName);
        await LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.WaitAsync(ct);
        try
        {
            await using var volumeLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            ct.ThrowIfCancellationRequested();
            // Check for link/reparse entries before a Git audit may
            // traverse the path. Never follow an outsider's symlink.
            if (new DirectoryInfo(original).LinkTarget is not null ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                return PreparedUnsafePath;
            var oldExists = Directory.Exists(original);
            var quarantinedExists = Directory.Exists(isolated);
            var oldFile = File.Exists(original);
            var quarantineFile = File.Exists(isolated);
            if (oldFile || quarantineFile)
                return PreparedUnsafePath;
            if (oldExists && quarantinedExists)
                return PreparedBothPresent;
            if (!oldExists && !quarantinedExists)
                return PreparedBothMissing;
            try
            {
                var before = await store.InspectExistingAsync(
                    stage.WorkpackId, ct);
                if (before.Sha256 != durable.BundleSha256 ||
                    before.Bytes != durable.BundleBytes)
                    throw new GitSourceSnapshotMirrorException(
                        "LaterCheckpointMirrorOrphanIsolationBundleChanged");
                if (oldExists)
                {
                    var physical = await new
                        LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
                            bundleRoot, mirrorRoot, enabled: true, clock: _clock)
                        .AuditAsync(stage, durable, ct);
                    if (physical.ObjectSetSha256 != intent.ObjectSetSha256 ||
                        physical.ObjectCount != intent.ObjectCount)
                        return PreparedOriginalCorrupt;
                }
                else
                {
                    // Two separate complete quarantined fsck/refs/OIDs
                    // graph passes, both under the same POSIX root
                    // lock, followed by immutable object-set equality.
                    var first = await
                        LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                            root, stage.PhysicalMirrorId,
                            decision.EvidenceSha256, stage.SourceRefs, ct);
                    var second = await
                        LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                            root, stage.PhysicalMirrorId,
                            decision.EvidenceSha256, stage.SourceRefs, ct);
                    if (first != second ||
                        second.Digest != intent.ObjectSetSha256 ||
                        second.Count != intent.ObjectCount)
                        return PreparedIsolatedCorrupt;
                }
                var after = await store.InspectExistingAsync(
                    stage.WorkpackId, ct);
                if (after != before ||
                    Directory.Exists(original) != oldExists ||
                    Directory.Exists(isolated) != quarantinedExists ||
                    File.Exists(original) || File.Exists(isolated) ||
                    new DirectoryInfo(original).LinkTarget is not null ||
                    new DirectoryInfo(isolated).LinkTarget is not null)
                    return PreparedUnsafePath;
                return oldExists
                    ? PreparedOriginalIntact : PreparedIsolatedIntact;
            }
            catch (GitObjectClosureAuditException)
            {
                return oldExists
                    ? PreparedOriginalCorrupt : PreparedIsolatedCorrupt;
            }
            catch (GitSourceSnapshotMirrorException)
            {
                return oldExists
                    ? PreparedOriginalCorrupt : PreparedIsolatedCorrupt;
            }
            catch (GitSourceBundleException)
            {
                return oldExists
                    ? PreparedOriginalCorrupt : PreparedIsolatedCorrupt;
            }
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }

    // #124: the only server-owned physical/SQL trust bridge. Every input
    // is re-sealed against the source-locked Prepared reader; NO caller
    // filename is accepted, and no destination GitHub API is invoked.
    // Holding the POSIX root lock *through* the SQL callback prevents
    // cooperating F-060 importers/recovery workers from changing paths
    // between double-fsck and terminal source-first SQL commit.
    public async Task<TResult> WithVerifiedIsolationAsync<TResult>(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection expected,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        GitIncrementalLaterCheckpointMirrorIsolationIntent prepared,
        Func<GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof,
            CancellationToken, Task<TResult>> persist,
        CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");
        ArgumentNullException.ThrowIfNull(persist);
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .VerifyForPhysicalIsolation(decision, expected);
        GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
            .Verify(prepared, expected, decision);
        if (stage.PhysicalMirrorId != expected.PhysicalMirrorId ||
            stage.WorkpackId != expected.WorkpackId ||
            stage.SourceSnapshotId != expected.SourceSnapshotId ||
            stage.CheckpointId != expected.CheckpointId ||
            stage.EvidenceSha256 != expected.StagingEvidenceSha256 ||
            stage.BundleSha256 != durable.BundleSha256 ||
            stage.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            expected.ObjectSetSha256 != prepared.ObjectSetSha256 ||
            expected.ObjectCount != prepared.ObjectCount ||
            prepared.IsolationDirectoryName !=
                GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                    .IsolationName(decision) ||
            prepared.CanReAdmit || prepared.CanMaterialize ||
            prepared.CanPush || prepared.CanRetry ||
            prepared.CanDispatch || prepared.MigrationComplete)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");

        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var store = new LocalGitBundleArtifactStore(bundleRoot);
        if (string.Equals(root, store.Root, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        var original = Path.Combine(root,
            stage.PhysicalMirrorId.ToString("N") + ".git");
        var isolated = Path.Combine(root, prepared.IsolationDirectoryName);

        await LocalGitSourceSnapshotMirrorMaterializer
            .ProcessGate.WaitAsync(ct);
        try
        {
            await using var rootLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            ct.ThrowIfCancellationRequested();
            // Both paths and foreign/symlinked files are ALWAYS
            // conflicting, even if bytes appear valid. A no-replace
            // rename protects unrelated operator-owned directories.
            if (new DirectoryInfo(original).LinkTarget is not null ||
                new DirectoryInfo(isolated).LinkTarget is not null ||
                File.Exists(original) || File.Exists(isolated))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorIsolationUnsafePath");
            var originalExists = Directory.Exists(original);
            var quarantinedExists = Directory.Exists(isolated);
            if (originalExists == quarantinedExists)
                throw new GitSourceSnapshotMirrorException(
                    originalExists
                        ? "LaterCheckpointMirrorIsolationBothPresent"
                        : "LaterCheckpointMirrorIsolationBothMissing");
            var before = await store.InspectExistingAsync(
                stage.WorkpackId, ct);
            if (before.Sha256 != durable.BundleSha256 ||
                before.Bytes != durable.BundleBytes)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorIsolationBundleChanged");
            if (originalExists)
            {
                // A pending PREPARED row does not by itself authorize
                // a rename of unverified objects. Re-audit UUID.git
                // under the exact importer POSIX root lock.
                var auditor =
                    new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
                        bundleRoot, mirrorRoot, enabled: true, clock: _clock);
                var physical = await auditor.AuditAsync(stage, durable, ct);
                if (physical.ObjectSetSha256 != prepared.ObjectSetSha256 ||
                    physical.ObjectCount != prepared.ObjectCount)
                    throw new GitSourceSnapshotMirrorException(
                        "LaterCheckpointMirrorIsolationOriginalChanged");
                ct.ThrowIfCancellationRequested();
                try
                {
                    // No fallback to an overwriting POSIX rename(2).
                    MoveDirectoryWithoutReplacement(original, isolated);
                }
                catch (IOException)
                {
                    throw new GitSourceSnapshotMirrorException(
                        "LaterCheckpointMirrorOrphanIsolationMoveFailed");
                }
            }

            // Whether we just moved UUID.git or found an already
            // quarantined path after a crash, a new proof needs TWO
            // complete Git object/ref/OID passes. Identical graph SHA
            // alone is never enough without the source-locked SQL
            // callback below.
            if (Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null ||
                !Directory.Exists(isolated) || File.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorIsolationPathsChanged");
            (int Count, string Digest) first;
            (int Count, string Digest) second;
            try
            {
                first = await
                    LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                        root, stage.PhysicalMirrorId,
                        decision.EvidenceSha256, stage.SourceRefs, ct);
                second = await
                    LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                        root, stage.PhysicalMirrorId,
                        decision.EvidenceSha256, stage.SourceRefs, ct);
            }
            catch (GitObjectClosureAuditException)
            {
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorIsolationClosureInvalid");
            }
            var after = await store.InspectExistingAsync(stage.WorkpackId, ct);
            if (first != second ||
                second.Count != prepared.ObjectCount ||
                second.Digest != prepared.ObjectSetSha256 ||
                after != before ||
                Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null ||
                !Directory.Exists(isolated) || File.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorIsolationPhysicalChanged");

            // Call back into a short source-first PostgreSQL CAS while
            // still holding BOTH physical locks. No Git subprocess or
            // filesystem move occurs in that database transaction.
            var proof = new
                GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof(
                    stage.PhysicalMirrorId, stage.WorkpackId,
                    prepared.IsolationDirectoryName,
                    second.Digest, second.Count,
                    before.Sha256, before.Bytes,
                    _clock.GetUtcNow(), !originalExists);
            return await persist(proof, ct);
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorIsolationBundleChanged");
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }

    // Read-only crash-diagnosis primitive. This method never restores
    // UUID.git, creates a new lease or returns authority. The only
    // filesystem path is derived from the reviewed decision SHA.
    public async Task<(int Count, string ObjectSetSha256)> AuditIsolatedAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        if (decision.Decision ==
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .ApprovedForIsolationReview)
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .VerifyForPhysicalIsolation(decision, inspection);
        else
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .Verify(decision, inspection);
        if (decision.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            inspection.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact ||
            inspection.PhysicalMirrorId != stage.PhysicalMirrorId ||
            inspection.WorkpackId != stage.WorkpackId ||
            inspection.SourceSnapshotId != stage.SourceSnapshotId ||
            inspection.CheckpointId != stage.CheckpointId ||
            inspection.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            inspection.BundleSha256 != durable.BundleSha256 ||
            inspection.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            inspection.FullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            inspection.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            !GitSourceBundleStorageCheck.ValidSha(inspection.ObjectSetSha256) ||
            inspection.ObjectCount is null or < 1)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");
        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var store = new LocalGitBundleArtifactStore(bundleRoot);
        if (string.Equals(root, store.Root, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        await LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.WaitAsync(ct);
        try
        {
            await using var rootLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            var original = Path.Combine(root,
                stage.PhysicalMirrorId.ToString("N") + ".git");
            if (Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationOriginalPresent");
            var before = await store.InspectExistingAsync(stage.WorkpackId, ct);
            if (before.Sha256 != durable.BundleSha256 ||
                before.Bytes != durable.BundleBytes)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationBundleChanged");
            (int Count, string Digest) checkedGraph;
            try
            {
                checkedGraph =
                    await LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                        root, stage.PhysicalMirrorId,
                        decision.EvidenceSha256, stage.SourceRefs, ct);
            }
            catch (GitObjectClosureAuditException)
            {
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationClosureInvalid");
            }
            var after = await store.InspectExistingAsync(stage.WorkpackId, ct);
            if (before != after ||
                checkedGraph.Count != inspection.ObjectCount ||
                checkedGraph.Digest != inspection.ObjectSetSha256)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationPhysicalChanged");
            return (checkedGraph.Count, checkedGraph.Digest);
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationBundleInvalid");
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }

    public async Task<GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt>
        IsolateAsync(
            GitIncrementalLaterCheckpointMirrorStagingPlan stage,
            GitIncrementalLaterCheckpointBundleReceipt durable,
            GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
            GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
            CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");

        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        if (decision.Decision ==
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .ApprovedForIsolationReview)
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .VerifyForPhysicalIsolation(decision, inspection);
        else
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .Verify(decision, inspection);
        if (decision.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            inspection.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                    .Intact ||
            inspection.PhysicalMirrorId != stage.PhysicalMirrorId ||
            inspection.SourceSnapshotId != stage.SourceSnapshotId ||
            inspection.CheckpointId != stage.CheckpointId ||
            inspection.WorkpackId != stage.WorkpackId ||
            inspection.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            inspection.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            inspection.BundleSha256 != durable.BundleSha256 ||
            inspection.FullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            inspection.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            !GitSourceBundleStorageCheck.ValidSha(
                inspection.ObjectSetSha256) ||
            inspection.ObjectCount is null or < 1 ||
            inspection.CanReAdmit || inspection.CanMaterialize ||
            inspection.CanPush || inspection.CanRetry ||
            inspection.CanDispatch || inspection.MigrationComplete)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");

        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var checkedBundleRoot = new LocalGitBundleArtifactStore(bundleRoot).Root;
        if (string.Equals(root, checkedBundleRoot, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        var basename = stage.PhysicalMirrorId.ToString("N") + ".git";
        var isolatedName =
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                .IsolationName(decision);
        var original = Path.Combine(root, basename);
        var isolated = Path.Combine(root, isolatedName);

        // Share exactly the F-060 importer process+POSIX volume lock.
        // No other importer in this application or POSIX process may
        // recreate/remove the physical UUID.git while we inspect+move.
        await LocalGitSourceSnapshotMirrorMaterializer
            .ProcessGate.WaitAsync(ct);
        try
        {
            await using var volumeLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(isolated) || File.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationAlreadyPresent");
            if (!Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null ||
                (File.GetAttributes(original) &
                    FileAttributes.ReparsePoint) != 0)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationSourceInvalid");

            // Rehash UUID.bundle and independently double-fsck every
            // source branch/tag/object closure WHILE the same POSIX
            // lock protects the final path. The old inspection's SHA
            // is not accepted as physical proof without this reread.
            var auditor =
                new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
                    bundleRoot, mirrorRoot, enabled: true, clock: _clock);
            var now = await auditor.AuditAsync(stage, durable, ct);
            if (now.ObjectSetSha256 != inspection.ObjectSetSha256 ||
                now.ObjectCount != inspection.ObjectCount ||
                now.PhysicalMirrorId != inspection.PhysicalMirrorId)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationPhysicalChanged");

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(isolated) || File.Exists(isolated))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationAlreadyPresent");
            try
            {
                // Same-volume atomic rename. NO deletion, no foreign
                // path, no overwrite and no Git invocation after move.
                MoveDirectoryWithoutReplacement(original, isolated);
            }
            catch (IOException)
            {
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationMoveFailed");
            }
            if (Directory.Exists(original) || !Directory.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationMoveFailed");
            var receipt =
                GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                    .Seal(inspection, decision, isolatedName,
                        _clock.GetUtcNow());
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity.Verify(
                receipt, inspection, decision);
            return receipt;
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationBundleInvalid");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationLocalIoFailure");
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }
}
