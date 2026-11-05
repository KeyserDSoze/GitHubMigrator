import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, RefreshCw, ShieldAlert, Users } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary, type MigrationJob, type StagingSyncResult, type StagingChangeSummary, type IncrementalPreparation, type IncrementalReview, type IncrementalTargetPreflight, type IncrementalOperationPlan, type DiscoveryRefIngestProgress, type DiscoveryRefManifest, type DiscoveryIdentityProgress, type DiscoveryGroupProgress, type DiscoveryMembershipProgress, type DiscoveryAclProgress } from './api';

type Tab = 'projects' | 'users' | 'security' | 'issues';

export default function DiscoveryPage({ token, isAdmin, isSuperAdmin = false }: {
  token: string; isAdmin: boolean; isSuperAdmin?: boolean;
}) {
  const { t } = useTranslation();
  const [sources, setSources] = useState<Connection[]>([]);
  const [destinations, setDestinations] = useState<Connection[]>([]);
  const [destinationId, setDestinationId] = useState('');
  const [sourceId, setSourceId] = useState('');
  const [summaries, setSummaries] = useState<DiscoverySummary[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [detail, setDetail] = useState<DiscoveryDetail | null>(null);
  const [staging, setStaging] = useState<StagingSyncResult | null>(null);
  const [changeSummary, setChangeSummary] = useState<StagingChangeSummary | null>(null);
  const [preparation, setPreparation] = useState<IncrementalPreparation | null>(null);
  const [review, setReview] = useState<IncrementalReview | null>(null);
  const [targetPreflight, setTargetPreflight] =
    useState<IncrementalTargetPreflight | null>(null);
  const [operationPlan, setOperationPlan] = useState<IncrementalOperationPlan | null>(null);
  const [targetSelections, setTargetSelections] =
    useState<Record<string, { selected: boolean; name: string }>>({});
  const [riskAcknowledged, setRiskAcknowledged] = useState(false);
  const [reviewReason, setReviewReason] = useState('');
  const [job, setJob] = useState<MigrationJob | null>(null);
  const [refProgress, setRefProgress] = useState<DiscoveryRefIngestProgress | null>(null);
  const [identityProgress, setIdentityProgress] = useState<DiscoveryIdentityProgress | null>(null);
  const [groupProgress, setGroupProgress] = useState<DiscoveryGroupProgress | null>(null);
  const [membershipProgress, setMembershipProgress] = useState<DiscoveryMembershipProgress | null>(null);
  const [aclProgress, setAclProgress] = useState<DiscoveryAclProgress | null>(null);
  const [refManifest, setRefManifest] = useState<DiscoveryRefManifest | null>(null);
  const [tab, setTab] = useState<Tab>('projects');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const pendingDiscoveryRequest = useRef<string | null>(null);

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    let active = true;
    Promise.all([api.listConnections(token), api.listDiscoveries(token)])
      .then(([connections, history]) => {
        if (!active) return;
        const available = connections.filter(c =>
          c.providerKey === 'azure-devops-services' && c.direction === 'source');
        setSources(available);
        const targets = connections.filter(c =>
          c.providerKey === 'github-enterprise-cloud' &&
          c.direction === 'destination' && c.validationCode === 'Verified');
        setDestinations(targets);
        setDestinationId(targets[0]?.id ?? '');
        if (available.length) {
          setSourceId(available[0].id);
          // Recover queued/running discovery state even if no snapshot exists yet.
          void api.listMigrationJobs(token, available[0].id).then(jobs => {
            if (!active) return;
            setJob(existing =>
              jobs.find(x => x.kind === 'DiscoverSource' &&
                (x.state === 'Queued' || x.state === 'Running')) ?? existing);
          }).catch(() => {});
        }
        setSummaries(history);
        if (history.length) {
          setSelectedId(history[0].id);
          void Promise.all([
            api.getDiscovery(token, history[0].id),
            api.getStagingSync(token, history[0].id).catch(() => null),
            api.listMigrationJobs(token, history[0].connectionId).catch(() => [])
          ]).then(([value, stage, jobs]) => {
            if (active) {
              setDetail(value); setStaging(stage);
              setJob(previous => previous?.kind === 'DiscoverSource' &&
                (previous.state === 'Queued' || previous.state === 'Running')
                  ? previous : jobs.find(x => x.kind === 'DiscoverSource' &&
                    (x.state === 'Queued' || x.state === 'Running')) ??
                    jobs.find(x => x.snapshotId === history[0].id) ?? null);
            }
          })
            .catch(() => { if (active) setError(t('operationError')); });
        }
      }).catch(() => { if (active) setError(t('operationError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, isAdmin, t]);

  async function viewSnapshot(id: string) {
    setSelectedId(id); setDetail(null); setStaging(null); setJob(null); setTab('projects'); setError('');
    setPreparation(null); setReview(null); setTargetPreflight(null);
    setOperationPlan(null);
    setRiskAcknowledged(false); setReviewReason('');
    try {
      const data = await api.getDiscovery(token, id);
      const [sync, jobs] = await Promise.all([
        api.getStagingSync(token, id).catch(() => null),
        api.listMigrationJobs(token, data.connectionId).catch(() => [] as MigrationJob[])
      ]);
      setDetail(data); setStaging(sync);
      setJob(jobs.find(x => x.snapshotId === id) ?? null);
    } catch { setError(t('operationError')); }
  }

  async function scan() {
    if (!sourceId) return;
    setBusy(true); setError(''); setNotice(''); setRefProgress(null);
    setIdentityProgress(null); setGroupProgress(null); setMembershipProgress(null);
    setAclProgress(null);
    try {
      // Preserve the request ID after an uncertain network failure so retry
      // cannot enqueue a second scan while the first is being processed.
      const requestId = pendingDiscoveryRequest.current ?? crypto.randomUUID();
      pendingDiscoveryRequest.current = requestId;
      const accepted = await api.enqueueDiscoveryJob(token, sourceId, requestId);
      setJob(accepted);
      setNotice(t('discoveryQueued'));
    } catch { setError(t('discoveryQueueError')); }
    finally { setBusy(false); }
  }

  async function syncSnapshot() {
    if (!selectedId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const result = await api.syncStagingSnapshot(token, selectedId);
      setStaging(result);
      setNotice(t('stagingSaved'));
    } catch (e) {
      setError(e instanceof Error && e.message === 'StagingSnapshotOutOfOrder'
        ? t('stagingStale') : t('stagingError'));
    } finally { setBusy(false); }
  }

  async function prepareIncremental() {
    if (!selectedId || !destinationId || !staging) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const run = await api.enqueueIncrementalPreparation(token, selectedId, destinationId);
      setPreparation(run);
      setReview(null); setTargetPreflight(null); setOperationPlan(null);
      setRiskAcknowledged(false); setReviewReason('');
      setNotice(t('incrementalQueued'));
    } catch (e) {
      setError(e instanceof Error && e.message === 'IncrementalSnapshotSuperseded'
        ? t('incrementalSuperseded') : t('incrementalError'));
    } finally { setBusy(false); }
  }

  async function incrementalDryRun() {
    if (!preparation || preparation.state !== 'Prepared') return;
    setBusy(true); setError(''); setNotice('');
    try {
      const result = await api.runIncrementalDryRun(token, preparation.id);
      setReview(result);
      setRiskAcknowledged(false);
      setReviewReason('');
      setNotice(t('incrementalDryRunVerified'));
    } catch { setError(t('incrementalReviewError')); }
    finally { setBusy(false); }
  }

  async function decideReview(decision: 'ApproveForPlanning' | 'Reject' | 'Revoke') {
    if (!preparation || !review || !review.isCurrent) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const updated = decision === 'Revoke'
        ? await api.revokeIncrementalReview(token, preparation.id,
            review.dryRunSha256, reviewReason)
        : await api.decideIncrementalReview(token, preparation.id, decision,
            review.dryRunSha256, riskAcknowledged, reviewReason || undefined);
      setReview(updated);
      setNotice(t('incrementalReviewSaved'));
    } catch { setError(t('incrementalReviewError')); }
    finally { setBusy(false); }
  }

  async function runTargetPreflight() {
    if (!preparation || review?.state !== 'ApprovedForPlanning' ||
        !review.isCurrent || !detail) return;
    const selections = detail.snapshot.projects.flatMap(p => p.repositories)
      .filter(r => r.versionControl === 0 && targetSelections[r.providerId]?.selected)
      .map(r => ({ sourceRepositoryId: r.providerId,
        targetRepositoryName: targetSelections[r.providerId].name.trim(),
        strategy: 0, include: true }));
    if (selections.length < 1 || selections.length > 10) {
      setError(t('targetPreflightSelectionLimit')); return;
    }
    setBusy(true); setError(''); setNotice('');
    try {
      const result = await api.runLiveTargetPreflight(
        token, preparation.id, review.dryRunSha256, selections);
      setTargetPreflight(result);
      setOperationPlan(null);
      setNotice(t('targetPreflightSaved'));
    } catch { setError(t('targetPreflightError')); }
    finally { setBusy(false); }
  }

  async function createOperationalPlan() {
    if (!preparation || !targetPreflight?.isCurrent ||
        review?.state !== 'ApprovedForPlanning') return;
    setBusy(true); setError(''); setNotice('');
    try {
      const plan = await api.createIncrementalOperationPlan(
        token, preparation.id, targetPreflight.evidenceSha256);
      setOperationPlan(plan);
      setNotice(t('operationPlanSaved'));
    } catch { setError(t('operationPlanError')); }
    finally { setBusy(false); }
  }

  async function enqueueStagingJob() {
    if (!selectedId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const accepted = await api.enqueueStagingJob(token, selectedId);
      setJob(accepted);
      if (accepted.result) setStaging(accepted.result);
      setNotice(t('jobAccepted'));
    } catch { setError(t('jobQueueError')); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    if (!preparation || (preparation.state !== 'Queued' && preparation.state !== 'Running')) return;
    let active = true;
    const timer = window.setInterval(() => {
      void api.getIncrementalPreparation(token, preparation.id)
        .then(value => { if (active) setPreparation(value); })
        .catch(() => {});
    }, 2000);
    return () => { active = false; window.clearInterval(timer); };
  }, [preparation?.id, preparation?.state, token]);

  useEffect(() => {
    if (!detail) { setTargetSelections({}); return; }
    const entries = detail.snapshot.projects.flatMap(p => p.repositories)
      .filter(r => r.versionControl === 0).slice(0, 100);
    setTargetSelections(Object.fromEntries(entries.map((r, index) =>
      [r.providerId, { selected: index < 10,
        name: /^[A-Za-z0-9._-]{1,100}$/.test(r.name) &&
          !r.name.toLowerCase().endsWith('.git') ? r.name :
          ('migrated-' + r.providerId.replace(/[^A-Za-z0-9._-]/g, '-')).slice(0, 100) }])));
  }, [detail?.id]);

  useEffect(() => {
    if (review?.state !== 'ApprovedForPlanning' || !preparation ||
        !review.isCurrent) {
      setTargetPreflight(null); return;
    }
    let active = true;
    void api.getLiveTargetPreflight(token, preparation.id)
      .then(result => { if (active) setTargetPreflight(result); })
      .catch(() => { if (active) setTargetPreflight(null); });
    return () => { active = false; };
  }, [preparation?.id, review?.state, review?.isCurrent, token]);

  useEffect(() => {
    if (!preparation || !targetPreflight ||
        review?.state !== 'ApprovedForPlanning') {
      setOperationPlan(null); return;
    }
    let active = true;
    void api.listIncrementalOperationPlans(token, preparation.id)
      .then(items => { if (active) setOperationPlan(items[0] ?? null); })
      .catch(() => { if (active) setOperationPlan(null); });
    return () => { active = false; };
  }, [preparation?.id, targetPreflight?.evidenceSha256, review?.state, token]);

  useEffect(() => {
    if (!preparation || preparation.state !== 'Prepared') {
      setReview(null);
      return;
    }
    let active = true;
    void api.getIncrementalReview(token, preparation.id)
      .then(result => { if (active) setReview(result); })
      .catch(() => { if (active) setReview(null); });
    return () => { active = false; };
  }, [preparation?.id, preparation?.state, token]);

  useEffect(() => {
    if (!job || (job.state !== 'Queued' && job.state !== 'Running')) return;
    let active = true;
    const timer = window.setInterval(() => {
      if (job.kind === 'DiscoverSource') {
        void api.getDiscoveryRefProgress(token, job.id).then(progress => {
          if (active) setRefProgress(progress);
        }).catch(() => {}); // Status polling remains usable if progress endpoint is unavailable.
        void api.getDiscoveryIdentityProgress(token, job.id).then(progress => {
          if (active) setIdentityProgress(progress);
        }).catch(() => {});
        void api.getDiscoveryGroupProgress(token, job.id).then(progress => {
          if (active) setGroupProgress(progress);
        }).catch(() => {});
        void api.getDiscoveryMembershipProgress(token, job.id).then(progress => {
          if (active) setMembershipProgress(progress);
        }).catch(() => {});
        void api.getDiscoveryAclProgress(token, job.id).then(progress => {
          if (active) setAclProgress(progress);
        }).catch(() => {});
      }
      void api.getMigrationJob(token, job.id).then(current => {
        if (!active) return;
        setJob(current);
        if (current.state === 'Succeeded' && current.result) setStaging(current.result);
        if (current.kind === 'DiscoverSource' &&
            (current.state === 'Succeeded' || current.state === 'Failed')) {
          pendingDiscoveryRequest.current = null;
          if (current.state === 'Succeeded' &&
              current.snapshotId !== '00000000-0000-0000-0000-000000000000') {
            void api.listDiscoveries(token).then(items => {
              if (active) setSummaries(items);
            }).catch(() => {});
            void viewSnapshot(current.snapshotId);
          }
        }
      }).catch(() => { if (active) setError(t('jobRefreshError')); });
    }, 2000);
    return () => { active = false; window.clearInterval(timer); };
  }, [job?.id, job?.state, token, t]);

  useEffect(() => {
    if (!job || job.kind !== 'DiscoverSource' || job.state !== 'Succeeded') {
      setRefManifest(null);
      return;
    }
    let active = true;
    void api.getDiscoveryRefManifest(token, job.id).then(value => {
      if (active) setRefManifest(value);
    }).catch(() => { if (active) setRefManifest(null); });
    return () => { active = false; };
  }, [job?.id, job?.kind, job?.state, token]);

  // A legacy sync has no F-025 journal; the 409 response is intentional and
  // must not be presented as an empty delta. Avoid mixing selected snapshots.
  useEffect(() => {
    if (!staging || staging.snapshotId !== selectedId) {
      setChangeSummary(null);
      return;
    }
    let active = true;
    void api.getStagingChangeSummary(token, staging.snapshotId)
      .then(delta => { if (active) setChangeSummary(delta); })
      .catch(() => { if (active) setChangeSummary(null); });
    return () => { active = false; };
  }, [staging?.snapshotId, selectedId, token]);

  if (!isAdmin) return <section className="empty-card"><ShieldAlert size={30} /><p>{t('discoveryRequiresAdmin')}</p></section>;

  const summary = summaries.find(x => x.id === selectedId);
  const groups = detail?.groups ?? [];
  const memberships = detail?.memberships ?? [];
  const acls = detail?.aclEvidence ?? [];
  const displayPrincipal = (descriptor: string) => {
    const group = groups.find(g => g.legacyDescriptor === descriptor);
    if (group) return group.displayName;
    const user = detail?.snapshot.identities.find(i => i.legacyDescriptor === descriptor);
    return user?.displayName || descriptor;
  };
  const displayMember = (descriptor: string) =>
    detail?.snapshot.identities.find(i => i.providerId === descriptor)?.displayName ||
    groups.find(g => g.descriptor === descriptor)?.displayName || descriptor;
  const showRead = (mask: number) => Math.floor(mask / 2) % 2 === 1;
  return <div>
    <section className="discovery-top" aria-label={t('discoverySource')}>
      <label>{t('discoverySource')}
        <select aria-label={t('discoverySource')} value={sourceId} onChange={e => setSourceId(e.target.value)}>
          {!sources.length && <option value="">{t('discoveryNoSource')}</option>}
          {sources.map(c => <option value={c.id} key={c.id}>{c.name} · {c.organization}</option>)}
        </select>
      </label>
      <button className="primary" disabled={busy || loading || !sourceId} type="button" onClick={() => { void scan(); }}>
        <RefreshCw size={16} /> {busy ? t('discoveryBusy') : t('discoveryQueueStart')}
      </button>
      <button className="plan-secondary" disabled={busy || loading || !selectedId} type="button"
        onClick={() => { void syncSnapshot(); }}>
        <RefreshCw size={16} /> {t('stagingSync')}
      </button>
      <button className="plan-secondary" disabled={busy || loading || !selectedId} type="button"
        onClick={() => { void enqueueStagingJob(); }}>
        <RefreshCw size={16} /> {t('jobEnqueue')}
      </button>
      <label>{t('incrementalTarget')}
        <select value={destinationId} aria-label={t('incrementalTarget')}
          onChange={e => setDestinationId(e.target.value)}>
          {!destinations.length && <option value="">{t('incrementalNoTarget')}</option>}
          {destinations.map(c => <option key={c.id} value={c.id}>
            {c.name} · {c.organization}
          </option>)}
        </select>
      </label>
      <button className="plan-secondary" type="button"
        disabled={busy || loading || !staging || !selectedId || !destinationId}
        onClick={() => { void prepareIncremental(); }}>
        <CheckCircle2 size={16} /> {t('incrementalPrepare')}
      </button>
    </section>
    <div className="discovery-note">{t('discoveryNotReady')}</div>
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    {job && <section className="notice" role="status" aria-label={t('jobStatusTitle')}>
      <strong>{job.kind === 'DiscoverSource' ? t('discoveryJobStatusTitle') : t('jobStatusTitle')}</strong> · {t('jobState' + job.state)}
      {' · '}{job.attempts}/{job.maxAttempts} {t('jobAttempts')}
      {job.lastErrorCode && <> · {t('jobErrorCode')}: {job.lastErrorCode}</>}
    </section>}
    {job?.kind === 'DiscoverSource' && refProgress?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryRefProgressTitle')}>
        <strong>{t('discoveryRefProgressTitle')}</strong>
        {' · '}{refProgress.observedRefs} {t('discoveryRefProgressRefs')}
        {' · '}{refProgress.checkpointedRefPages} {t('discoveryRefProgressPages')}
        {' · '}{t('discoveryRefProgressProvisional')}
      </section>}
    {job?.kind === 'DiscoverSource' && identityProgress?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryIdentityProgressTitle')}>
        <strong>{t('discoveryIdentityProgressTitle')}</strong>
        {' · '}{identityProgress.observedUsers} {t('discoveryIdentityProgressUsers')}
        {' · '}{identityProgress.checkpointedUserPages} {t('discoveryIdentityProgressPages')}
        {' · '}{job.state === 'Succeeded'
          ? t('discoveryIdentityProgressVerified') : t('discoveryRefProgressProvisional')}
      </section>}
    {job?.kind === 'DiscoverSource' && aclProgress?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryAclProgressTitle')}>
        <strong>{t('discoveryAclProgressTitle')}</strong>
        {' · '}{aclProgress.observedAces} {t('discoveryAclProgressAces')}
        {' · '}{aclProgress.checkpointedAclPages} {t('discoveryRefProgressPages')}
        {' · '}{job.state === 'Succeeded'
          ? t('discoveryGroupProgressVerified') : t('discoveryRefProgressProvisional')}
      </section>}
    {job?.kind === 'DiscoverSource' && membershipProgress?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryMemberships')}>
        <strong>{t('discoveryMemberships')}</strong>
        {' · '}{membershipProgress.observedMemberships}
        {' · '}{membershipProgress.checkpointedMembershipPages} {t('discoveryRefProgressPages')}
        {' · '}{job.state === 'Succeeded'
          ? t('discoveryGroupProgressVerified') : t('discoveryRefProgressProvisional')}
      </section>}
    {job?.kind === 'DiscoverSource' && groupProgress?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryGroupProgressTitle')}>
        <strong>{t('discoveryGroupProgressTitle')}</strong>
        {' · '}{groupProgress.observedGroups} {t('discoveryGroupProgressGroups')}
        {' · '}{groupProgress.checkpointedGroupPages} {t('discoveryGroupProgressPages')}
        {' · '}{job.state === 'Succeeded'
          ? t('discoveryGroupProgressVerified') : t('discoveryRefProgressProvisional')}
      </section>}
    {job?.kind === 'DiscoverSource' && job.state === 'Succeeded' &&
      refManifest?.jobId === job.id &&
      <section className="notice" role="status" aria-label={t('discoveryManifestTitle')}>
        <strong>{t('discoveryManifestTitle')}</strong>
        {' · '}{refManifest.assetCount} {t('discoveryManifestAssets')}
        {' · '}{refManifest.chunkCount} {t('discoveryManifestChunks')}
        {' · '}{t('discoveryManifestDigest')} {refManifest.rootSha256.slice(0, 12)}…
      </section>}
    {staging && <section className="notice" role="status" aria-label={t('stagingStatus')}>
      <strong>{t('stagingStatus')}</strong> · {staging.observed} {t('stagingObserved')}
      {' · +'}{staging.added} {t('stagingAdded')}
      {' · ~'}{staging.changed} {t('stagingChanged')}
      {' · '}{staging.markedMissing} {t('stagingMissing')}
      {' · '}{staging.sourceInventoryComplete ? t('stagingCoverageComplete') : t('stagingCoveragePartial')}
    </section>}
    {preparation?.snapshotId === selectedId && <section
      className="notice" role="status" aria-label={t('incrementalStatusTitle')}>
      <strong>{t('incrementalStatusTitle')}</strong> ·
      {' '}{preparation.state === 'Prepared'
        ? t('incrementalPrepared') : preparation.state === 'Failed'
          ? t('incrementalFailed') : t('incrementalInProgress')}
      {' · '}{preparation.attempts}/{preparation.maxAttempts} {t('jobAttempts')}
      {preparation.state === 'Prepared' && <>
        {' · '}{preparation.itemCount} {t('incrementalReviewItems')}
        {' · '}{preparation.sensitiveItemCount} {t('incrementalSensitiveItems')}
        {' · '}{t('incrementalDigest')} {preparation.evidenceSha256?.slice(0, 12)}…
      </>}
      {preparation.lastErrorCode && <> · {t('jobErrorCode')}: {preparation.lastErrorCode}</>}
    </section>}
    {preparation?.snapshotId === selectedId &&
      preparation.state === 'Prepared' && <section className="notice"
        aria-label={t('incrementalReviewTitle')}>
        <strong>{t('incrementalReviewTitle')}</strong> · {t('incrementalNoWrites')}
        <div>
          <button type="button" className="plan-secondary"
            disabled={busy || review?.state === 'ApprovedForPlanning' ||
              review?.state === 'Rejected' || review?.state === 'Revoked'}
            onClick={() => { void incrementalDryRun(); }}>
            {t('incrementalRunDryRun')}
          </button>
        </div>
        {review && <>
          <p>{t('incrementalReviewState')}: {review.state}
            {' · '}{review.itemCount} {t('incrementalReviewItemCount')}
            {' · '}{review.sensitiveItemCount} {t('incrementalSensitiveItems')}
            {' · '}{review.removalItemCount} {t('incrementalRemovals')}
          </p>
          <p>{t('incrementalDryRunDigest')}: <code>{review.dryRunSha256.slice(0, 16)}…</code>
            {' · '}{review.isCurrent
              ? t('incrementalDryRunCurrent') : t('incrementalDryRunStale')}
          </p>
          {review.state === 'DryRunReady' && review.isCurrent && <>
            {review.sensitiveItemCount > 0 && <label>
              <input type="checkbox" checked={riskAcknowledged}
                onChange={event => setRiskAcknowledged(event.target.checked)} />
              {' '}{t('incrementalAcknowledgeSecurity')}
            </label>}
            <label>{t('incrementalReviewReason')}
              <input type="text" value={reviewReason} maxLength={48}
                placeholder={t('incrementalReasonPlaceholder')}
                onChange={event => setReviewReason(event.target.value)} />
            </label>
            {isSuperAdmin && <button type="button" className="plan-secondary"
              disabled={busy || (review.sensitiveItemCount > 0 && !riskAcknowledged)}
              onClick={() => { void decideReview('ApproveForPlanning'); }}>
              {t('incrementalApproveForPlanning')}
            </button>}
            <button type="button" className="plan-secondary"
              disabled={busy || !/^[A-Za-z0-9]{1,48}$/.test(reviewReason)}
              onClick={() => { void decideReview('Reject'); }}>
              {t('incrementalReject')}
            </button>
          </>}
          {review.state === 'ApprovedForPlanning' && isSuperAdmin && <>
            <label>{t('incrementalReviewReason')}
              <input type="text" value={reviewReason} maxLength={48}
                onChange={event => setReviewReason(event.target.value)} />
            </label>
            <button type="button" className="plan-secondary"
              disabled={busy || !/^[A-Za-z0-9]{1,48}$/.test(reviewReason)}
              onClick={() => { void decideReview('Revoke'); }}>
              {t('incrementalRevoke')}
            </button>
          </>}
        </>}
      </section>}
    {preparation?.snapshotId === selectedId &&
      review?.state === 'ApprovedForPlanning' && <section
        className="notice" aria-label={t('targetPreflightTitle')}>
        <strong>{t('targetPreflightTitle')}</strong>
        <p>{t('targetPreflightSafety')}</p>
        {detail?.snapshot.projects.flatMap(p => p.repositories)
          .filter(r => r.versionControl === 0 && targetSelections[r.providerId])
          .slice(0, 100).map(r => <label key={r.providerId}>
            <input type="checkbox" checked={targetSelections[r.providerId]?.selected ?? false}
              onChange={event => setTargetSelections(old => ({
                ...old, [r.providerId]: {
                  ...old[r.providerId], selected: event.target.checked
                }
              }))} />
            {' '}{r.name} · {t('targetPreflightName')}:
            <input type="text" maxLength={100}
              value={targetSelections[r.providerId]?.name ?? ''}
              onChange={event => setTargetSelections(old => ({
                ...old, [r.providerId]: {
                  ...old[r.providerId], name: event.target.value
                }
              }))} />
          </label>)}
        <button className="plan-secondary" type="button"
          disabled={busy || !review.isCurrent}
          onClick={() => { void runTargetPreflight(); }}>
          {t('targetPreflightRun')}
        </button>
        {targetPreflight && <>
          <p><strong>{targetPreflight.state === 'Blocked'
            ? t('targetPreflightBlocked') : t('targetPreflightUnknown')}</strong>
            {' · '}{targetPreflight.checkedRepositories} {t('targetPreflightRepositories')}
            {' · '}{targetPreflight.visibleCollisions} {t('targetPreflightCollisions')}
            {' · '}{targetPreflight.uncertainRepositories} {t('targetPreflightUncertain')}
            {' · '}{targetPreflight.isCurrent
              ? t('incrementalDryRunCurrent') : t('incrementalDryRunStale')}
          </p>
          <p>{t('targetPreflightMembership')}: {targetPreflight.membershipStatus}
            {' · '}{t('incrementalDigest')}: {targetPreflight.evidenceSha256.slice(0, 12)}…
          </p>
          {targetPreflight.repositories.map(r => <p key={r.sourceRepositoryId}>
            {r.targetRepositoryName}: {r.status}
          </p>)}
        </>}
      </section>}
    {preparation?.snapshotId === selectedId &&
      review?.state === 'ApprovedForPlanning' && targetPreflight && <section
        className="notice" aria-label={t('operationPlanTitle')}>
        <strong>{t('operationPlanTitle')}</strong>
        <p>{t('operationPlanSafety')}</p>
        <button type="button" className="plan-secondary"
          disabled={busy || !targetPreflight.isCurrent}
          onClick={() => { void createOperationalPlan(); }}>
          {t('operationPlanBuild')}
        </button>
        {operationPlan && <>
          <p>{t('operationPlanDigest')}: <code>
            {operationPlan.manifestSha256.slice(0, 16)}…
          </code>
            {' · '}{operationPlan.repositoryCount} {t('operationPlanRepositories')}
            {' · '}{operationPlan.stepCount} {t('operationPlanSteps')}
            {' · '}{operationPlan.changedRefCount} {t('operationPlanChangedRefs')}
            {' · '}{operationPlan.isCurrent
              ? t('incrementalDryRunCurrent') : t('incrementalDryRunStale')}
          </p>
          <p>{t('operationPlanBlockers')}: {operationPlan.blockers.join(', ')}</p>
          <details>
            <summary>{t('operationPlanSteps')}</summary>
            <ol>{operationPlan.steps.map(s => <li key={s.ordinal}>
              {s.sourceRepositoryId} → {s.targetRepositoryName}:
              {' '}{s.kind} ({s.blockerCode})
            </li>)}</ol>
          </details>
        </>}
      </section>}
    {changeSummary?.snapshotId === selectedId && <section
      className="notice" role="status" aria-label={t('stagingChangeJournalTitle')}>
      <strong>{t('stagingChangeJournalTitle')}</strong>
      {' · +'}{changeSummary.added} {t('stagingAdded')}
      {' · ~'}{changeSummary.changed} {t('stagingChanged')}
      {' · ↺'}{changeSummary.restored} {t('stagingRestored')}
      {' · −'}{changeSummary.missing} {t('stagingMissing')}
    </section>}
    {summary && <div className="discovery-summary">
      {[
        [t('discoveryProjectCount'), summary.projectCount],
        [t('discoveryRepositoryCount'), summary.repositoryCount],
        [t('discoveryRefCount'), summary.refCount],
        [t('discoveryUserCount'), summary.identityCount]
      ].map(([title, value]) => <div key={title} className="discovery-metric">
        <strong>{value}</strong><small>{title}</small>
      </div>)}
    </div>}
    {summary?.refsExternalized && <p className="muted" role="note">
      {t('discoveryExternalRefsNotice')}
    </p>}
    {summary?.identitiesExternalized && <p className="muted" role="note">
      {t('discoveryExternalIdentitiesNotice')}
    </p>}
    {summary?.groupsExternalized && <p className="muted" role="note">
      {t('discoveryExternalGroupsNotice')}
    </p>}
    {summary && <div className="discovery-notice"><ShieldAlert size={17} />
      <span>{t('discoveryPartial')}. {t('discoveryPermissionWarning')}</span>
    </div>}
    <div className="discovery-security-stats" aria-label={t('discoverySecurityEvidence')}>
      <div>{t('discoveryGroups')}: <strong>{summary?.groupCount ?? groups.length}</strong></div>
      <div>{t('discoveryMemberships')}: <strong>{summary?.membershipCount ?? memberships.length}</strong></div>
      <div>{t('discoveryAcls')}: <strong>{summary?.aclEntryCount ?? acls.length}</strong></div>
      <span>{t('discoveryEffectiveWarning')}</span>
    </div>
    <div className="discovery-grid">
      <aside className="discovery-history">
        <h2>{t('discoverySelect')}</h2>
        {loading ? <p>{t('discoveryBusy')}</p> : summaries.length === 0 ? <p className="muted">{t('discoveryEmpty')}</p> :
          summaries.map(item => <button key={item.id} aria-current={item.id === selectedId} type="button"
            onClick={() => { void viewSnapshot(item.id); }}>
            <strong>{item.organization} · {item.projectCount} {t('discoveryProjectCount').toLowerCase()}</strong>
            <small>{new Date(item.collectedAtUtc).toLocaleString()} · {item.issueCount} {t('discoveryWarnings').toLowerCase()}</small>
          </button>)}
      </aside>
      <section className="discovery-content">
        <h2>{t('discoveryBrowse')}</h2>
        <div className="discovery-tab-row">
          {(['projects', 'users', 'security', 'issues'] as const).map(name => <button type="button" key={name}
            aria-pressed={tab === name} onClick={() => setTab(name)}>
            {name === 'projects' ? t('discoveryDetails') : name === 'users' ? t('discoveryUsers') : name === 'security' ? t('discoverySecurityEvidence') : t('discoveryWarnings')}
          </button>)}
        </div>
        {!detail ? <div className="discovery-empty"><p className="muted">{loading ? t('discoveryBusy') : t('discoveryEmpty')}</p></div> : <>
          {tab === 'projects' && (detail.snapshot.projects.length ?
            detail.snapshot.projects.map(project => <article className="discovery-project" key={project.providerId}>
              <h3>{project.name}</h3>
              {project.repositories.map(repo => {
                const repoRefs = detail.refs.filter(ref => ref.repositoryId === repo.providerId);
                return <div className="discovery-repo" key={repo.providerId}>
                  <div><strong>{repo.name}</strong>
                    <small>{repo.sourcePath ||
                      (detail.refsExternalized ? t('discoveryRefsStoredInDb') :
                        `${repoRefs.length} ${t('discoveryBranchCount')}`)}</small></div>
                  <span className="kind">{repo.versionControl === 1 ? t('discoveryTfvc') : t('discoveryGit')}</span>
                </div>;
              })}
            </article>) : <p className="muted">{t('discoveryNoProjects')}</p>)}
          {tab === 'users' && <>
            {!detail.identitiesComplete && <div className="discovery-warning"><ShieldAlert size={16} />{t('discoveryGraphIncomplete')}</div>}
            {detail.identitiesExternalized
              ? <p className="muted" role="note">
                  {t('discoveryExternalIdentitiesNotice')}
                  {' '}{detail.identityCount ?? 0} {t('discoveryUserCount')}.
                </p>
              : detail.snapshot.identities.length ? detail.snapshot.identities.map(identity =>
              <div className="discovery-identity" key={identity.providerId}>
                <strong><Users size={13} /> {identity.displayName}</strong>
                <small>{identity.email || identity.providerId}</small>
              </div>) : <p className="muted">{t('discoveryNoUsers')}</p>}
          </>}
          {tab === 'security' && <div className="discovery-security">
            <div className="discovery-warning" role="note"><ShieldAlert size={16} />
              <span>{t('discoveryEffectiveWarning')}</span></div>
            <div className="discovery-security-flags">
              <span>{t('discoveryGroups')}: {detail.securityCoverage?.groupsComplete ? t('discoveryCollected') : t('discoveryUnverified')}</span>
              <span>{t('discoveryMemberships')}: {detail.securityCoverage?.membershipsComplete ? t('discoveryCollected') : t('discoveryUnverified')}</span>
              <span>{t('discoveryAcls')}: {detail.securityCoverage?.aclsCollected ? t('discoveryCollected') : t('discoveryUnverified')}</span>
            </div>
            <h3>{t('discoveryGroups')}</h3>
            {detail.groupsExternalized
              ? <p className="muted" role="note">
                  {t('discoveryExternalGroupsNotice')} {' '}
                  {detail.groupCount ?? 0} {t('discoveryGroups').toLowerCase()}.
                </p>
              : groups.length === 0 && <p className="muted">{t('discoveryNoSecurity')}</p>}
            {detail.membershipsExternalized && <p className="muted">
              {t('discoveryMemberships')}: {detail.membershipCount ?? 0}
              {' · '}{t('discoveryExternalMembershipsNotice')}
            </p>}
            {groups.slice(0, 100).map(group => <div className="discovery-security-group" key={group.descriptor}>
              <strong>{group.displayName}</strong>
              <small>{group.legacyDescriptor || group.descriptor}</small>
              <span>{detail.membershipsExternalized
                ? t('discoveryMemberships') + ' · PostgreSQL'
                : memberships.filter(x => x.groupDescriptor === group.descriptor).length +
                    ' ' + t('discoveryMemberships').toLowerCase()}</span>
              <ul>{memberships.filter(x => x.groupDescriptor === group.descriptor).slice(0, 30).map(edge =>
                <li key={edge.memberDescriptor}>{displayMember(edge.memberDescriptor)}</li>)}</ul>
            </div>)}
            {groups.length > 100 && <p className="muted">{t('discoveryListCapped')}</p>}
            <h3>{t('discoveryAcls')}</h3>
            {detail.aclsExternalized && <p className="muted" role="note">
              {t('discoveryExternalAclsNotice')}
              {' '}{detail.aclCount ?? 0} {t('discoveryAclProgressAces')}.
            </p>}
            {acls.length === 0 && !detail.aclsExternalized &&
              <p className="muted">{t('discoveryNoSecurity')}</p>}
            {acls.slice(0, 150).map((entry, i) => {
              const repo = detail.snapshot.projects.flatMap(p => p.repositories).find(r => r.providerId === entry.repositoryId);
              return <div className="discovery-acl-row" key={i}>
                <div><strong>{repo?.name || entry.repositoryId}</strong><small>{entry.scope === 'project' ? t('discoveryProjectScope') : t('discoveryRepositoryScope')}</small></div>
                <div><span>{displayPrincipal(entry.securityDescriptor)}</span><small>{entry.inheritPermissions ? t('discoveryInherits') : t('discoveryNoInherit')}</small></div>
                <div><span>{t('discoveryAllow')} {entry.allowMask} · {t('discoveryDeny')} {entry.denyMask}</span>
                  <small>{t('discoveryReadBit')}: {showRead(entry.denyMask) ? t('discoveryDeny') : showRead(entry.allowMask) ? t('discoveryAllow') : t('discoveryNotSet')}</small></div>
              </div>;
            })}
            {acls.length > 150 && <p className="muted">{t('discoveryListCapped')}</p>}
          </div>}
          {tab === 'issues' && <>
            {detail.issues.map((issue, index) => <div className="discovery-warning" key={index}>
              <ShieldAlert size={16} /><span>{issue.code}{issue.projectId && ` · ${issue.projectId}`}</span>
            </div>)}
            {detail.issues.length === 0 && <div className="discovery-notice"><CheckCircle2 size={17} /> —</div>}
          </>}
        </>}
      </section>
    </div>
  </div>;
}
