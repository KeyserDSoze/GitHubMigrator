import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import SourceSyncCheckpointPanel from './SourceSyncCheckpointPanel';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle, ArrowRight, CheckCircle2, Clock3, CloudDownload,
  Database, GitBranch, Layers, LockKeyhole, RefreshCw, Search, ShieldCheck
} from 'lucide-react';
import {
  api, type IncrementalOperationPlan, type MigrationJob,
  type MultiRepositorySyncPulse, type RepositorySyncSourceDelta
} from './api';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const toDate = (date: string) => new Date(date).toLocaleString();
const failureCode = (error: unknown) =>
  error instanceof Error ? error.message : 'UnknownError';

type Props = {
  token: string; isSuperAdmin: boolean;
  initialPreparationId?: string; initialPlanId?: string;
  onOpenRecovery: (preparationId: string, planId: string) => void;
};

function IsolatedRepository({ row, onOpenRecovery }: {
  row: RepositorySyncSourceDelta; onOpenRecovery: () => void
}) {
  const { t } = useTranslation();
  const isMonitored = row.recoveryTracking !== 'NotMonitored';
  const hasMarker = row.recoveryTracking === 'PossibleSendEvidence';
  const needsAttention = row.recoveryState === 'OperatorReviewRequired';
  return <article className="sync-repo-card">
    <div className="sync-repo-head">
      <div className="sync-repo-identity">
        <div className="sync-repo-icon"><GitBranch size={20} /></div>
        <div><strong>{row.targetRepositoryName}</strong>
          <small>{t('syncSourceRepo')}: {row.sourceRepositoryId}</small></div>
      </div>
      <span className={'sync-repo-badge ' +
        (needsAttention ? 'sync-repo-badge-warning' :
          hasMarker ? 'sync-repo-badge-evidence' : '')}>
        {t('syncRecovery_' + row.recoveryTracking)}
      </span>
    </div>
    <div className="sync-ref-pills">
      <div><strong>{row.totalRefChanges}</strong><span>{t('syncTotalRefs')}</span></div>
      <div><strong>{row.addedRefs}</strong><span>{t('syncAdded')}</span></div>
      <div><strong>{row.changedRefs}</strong><span>{t('syncChanged')}</span></div>
      <div><strong>{row.restoredRefs}</strong><span>{t('syncRestored')}</span></div>
      <div><strong>{row.missingRefs}</strong><span>{t('syncMissing')}</span></div>
    </div>
    <div className="sync-repo-foot">
      <div className="sync-repo-alert">
        <LockKeyhole size={15} />
        <span>{t('syncBlocked')}: {row.stepBlocker}</span>
      </div>
      {hasMarker ? <div className="sync-recovery-link">
        <span className={needsAttention ? 'sync-alert-text' : ''}>
          {t('recoveryState_' + row.recoveryState)}
        </span>
        {row.canOpenRecovery &&
          <button type="button" className="icon-button"
            onClick={onOpenRecovery}>
            {t('syncOpenRecovery')} <ArrowRight size={15} />
          </button>}
      </div> : <span className="sync-muted-foot">
        {isMonitored
          ? t('syncNoMarkerHint') : t('syncNoMonitoringHint')}
      </span>}
    </div>
  </article>;
}

export default function RepositorySyncControlTowerPage({
  token, isSuperAdmin, initialPreparationId, initialPlanId, onOpenRecovery
}: Props) {
  const { t } = useTranslation();
  const [input, setInput] = useState(initialPreparationId ?? '');
  const [preparationId, setPreparationId] = useState('');
  const [plans, setPlans] = useState<IncrementalOperationPlan[]>([]);
  const [planId, setPlanId] = useState('');
  const [pulse, setPulse] = useState<MultiRepositorySyncPulse | null>(null);
  const [search, setSearch] = useState('');
  const [onlyChanges, setOnlyChanges] = useState(false);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [refreshJob, setRefreshJob] = useState<MigrationJob | null>(null);
  const requestSequence = useRef(0);

  const inspect = useCallback(async (preparation: string, plan: string) => {
    const sequence = ++requestSequence.current;
    setBusy('pulse'); setError('');
    try {
      const data = await api.getMultiRepositorySyncPulse(
        token, preparation, plan);
      if (sequence !== requestSequence.current) return;
      if (data.readyForExecution || data.canPush || data.canRetry ||
          data.migrationComplete || data.providerSourceFreshnessVerified ||
          data.repositories.some(row =>
            row.canPush || row.canRetry || row.migrationComplete))
        throw new Error('MultiRepositoryPulseUnsafeResponse');
      setPulse(data);
    } catch (e) {
      if (sequence === requestSequence.current) {
        setPulse(null);
        setError(t('syncLoadFailed') + ': ' + failureCode(e));
      }
    } finally {
      if (sequence === requestSequence.current) setBusy('');
    }
  }, [t, token]);

  const open = useCallback(async (id: string, preferredPlanId = '') => {
    if (!uuid.test(id.trim())) {
      setError(t('syncInvalidPreparation')); return;
    }
    const sequence = ++requestSequence.current;
    setBusy('plans'); setError(''); setNotice('');
    setPulse(null); setPlans([]); setPlanId(''); setPreparationId('');
    setRefreshJob(null); setSearch(''); setOnlyChanges(false);
    try {
      const found = await api.listIncrementalOperationPlans(
        token, id.trim());
      if (sequence !== requestSequence.current) return;
      const selected = found.find(x => x.id === preferredPlanId) ??
        found[0] ?? null;
      setPlans(found);
      setPreparationId(id.trim());
      setPlanId(selected?.id ?? '');
      if (selected) void inspect(id.trim(), selected.id);
    } catch (e) {
      if (sequence === requestSequence.current)
        setError(t('syncLoadFailed') + ': ' + failureCode(e));
    } finally {
      if (sequence === requestSequence.current) setBusy('');
    }
  }, [inspect, t, token]);

  useEffect(() => {
    if (isSuperAdmin && initialPreparationId) {
      setInput(initialPreparationId);
      void open(initialPreparationId, initialPlanId);
    }
    return () => { ++requestSequence.current; };
  }, [isSuperAdmin, initialPreparationId, initialPlanId, open]);

  const enqueueRefresh = async () => {
    if (!pulse || busy || !isSuperAdmin ||
        !window.confirm(t('syncRefreshConfirm'))) return;
    setBusy('refresh'); setError(''); setNotice('');
    try {
      // Existing F-008/F-013 durable worker. The operator *only*
      // requests a new Azure DevOps discovery + PostgreSQL staging;
      // it NEVER synchronizes GitHub or reopens the reviewed plan.
      const job = await api.enqueueDiscoveryJob(
        token, pulse.sourceConnectionId, crypto.randomUUID());
      setRefreshJob(job);
      setNotice(t('syncRefreshQueued'));
    } catch (e) {
      setError(t('syncRefreshFailed') + ': ' + failureCode(e));
    } finally { setBusy(''); }
  };

  const inspectRefreshJob = async () => {
    if (!refreshJob || busy) return;
    setBusy('job'); setError('');
    try {
      const job = await api.getMigrationJob(token, refreshJob.id);
      setRefreshJob(job);
      if (job.state === 'Succeeded') setNotice(t('syncRefreshFinished'));
    } catch (e) {
      setError(t('syncRefreshFailed') + ': ' + failureCode(e));
    } finally { setBusy(''); }
  };

  if (!isSuperAdmin) return <section className="empty-card" role="alert">
    <LockKeyhole size={29} />
    <h2>{t('accessDenied')}</h2>
    <p>{t('syncSuperAdminOnly')}</p>
  </section>;

  const visible = pulse?.repositories.filter(repo =>
    (!onlyChanges || repo.totalRefChanges > 0) &&
    (repo.sourceRepositoryId.toLowerCase().includes(search.toLowerCase()) ||
      repo.targetRepositoryName.toLowerCase().includes(search.toLowerCase()))
  ) ?? [];
  const latest = pulse?.stagingState === 'LatestRecordedSnapshot';
  const noSourceGuarantee = !pulse?.sourceInventoryComplete;

  return <section className="sync-control-tower">
    <div className="sync-caution">
      <ShieldCheck size={23} />
      <div><strong>{t('syncSafetyTitle')}</strong>
        <p>{t('syncSafetyBody')}</p></div>
      <span>{t('syncNoGitHubWrites')}</span>
    </div>

    <form className="sync-prep-search" onSubmit={(e: FormEvent) => {
      e.preventDefault(); void open(input);
    }}>
      <label htmlFor="sync-preparation">{t('syncPreparation')}</label>
      <div className="sync-prep-controls">
        <input id="sync-preparation" value={input}
          placeholder="UUID" onChange={e => setInput(e.target.value)} />
        <button type="submit" className="primary" disabled={!!busy}>
          <Search size={16} /> {t('syncOpen')}
        </button>
      </div>
    </form>

    {preparationId && <div className="sync-plan-selection">
      <label htmlFor="sync-plan">{t('syncPlan')}</label>
      <select id="sync-plan" value={planId} disabled={!!busy}
        onChange={e => {
          setPlanId(e.target.value); setPulse(null);
          setRefreshJob(null);
          void inspect(preparationId, e.target.value);
        }}>
        {plans.map(x => <option key={x.id} value={x.id}>
          {x.organization} · {x.id.slice(0, 8)} · {x.repositoryCount} {t('syncRepositories')}
        </option>)}
      </select>
      <button type="button" className="icon-button"
        disabled={!planId || !!busy}
        onClick={() => void inspect(preparationId, planId)}>
        <RefreshCw size={16} /> {t('syncReload')}
      </button>
    </div>}

    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    {busy === 'plans' || busy === 'pulse' ?
      <p className="muted" role="status">{t('syncLoading')}</p> : null}
    {preparationId && !plans.length && !busy &&
      <p className="muted">{t('syncNoPlans')}</p>}

    {pulse && <>
      <div className="sync-pulse-topline">
        <div><h2>{t('syncSummaryTitle')}</h2>
          <p>{t('syncSummaryIntro')}</p></div>
        <span className={'sync-staging-status ' +
          (latest ? 'sync-staging-current' : 'sync-staging-stale')}>
          {latest ? <CheckCircle2 size={16} /> : <AlertTriangle size={16} />}
          {latest ? t('syncLatestRecorded') : t('syncSuperseded')}
        </span>
      </div>
      <div className="sync-pulse-stats">
        <div><Layers size={20} />
          <strong>{pulse.repositories.length}</strong><span>{t('syncRepositories')}</span></div>
        <div><GitBranch size={20} />
          <strong>{pulse.repositories.reduce((sum, r) => sum + r.totalRefChanges, 0)}</strong>
          <span>{t('syncChangedRefs')}</span></div>
        <div><Database size={20} />
          <strong>{pulse.stagingObserved}</strong><span>{t('syncStagingObserved')}</span></div>
        <div><Clock3 size={20} />
          <strong>{pulse.stageJobState}</strong><span>{t('syncStageJob')}</span></div>
      </div>
      <div className="sync-provenance">
        <div><small>{t('syncSourceSnapshot')}</small>
          <code title={pulse.snapshotId}>{pulse.snapshotId.slice(0, 18)}…</code></div>
        <div><small>{t('syncSourceCaptured')}</small>
          <strong>{toDate(pulse.sourceCollectedAtUtc)}</strong></div>
        <div><small>{t('syncStagedAt')}</small>
          <strong>{toDate(pulse.stagingAppliedAtUtc)}</strong></div>
        <div><small>{t('syncManifest')}</small>
          <code title={pulse.manifestSha256}>{pulse.manifestSha256.slice(0, 18)}…</code></div>
      </div>
      {!latest && <div className="sync-warning" role="status">
        <AlertTriangle size={18} />
        <span>{t('syncSupersededDetails')} {toDate(pulse.latestKnownStagingAppliedAtUtc)}</span>
      </div>}
      {noSourceGuarantee && <div className="sync-warning" role="status">
        <AlertTriangle size={18} />
        <span>{t('syncPartialSource')}</span>
      </div>}
      <div className="sync-refresh-source">
        <div><h3><CloudDownload size={19} /> {t('syncNewDiscoveryTitle')}</h3>
          <p>{t('syncNewDiscoveryDescription')}</p></div>
        <button type="button" className="icon-button"
          disabled={!!busy} onClick={() => void enqueueRefresh()}>
          <CloudDownload size={16} /> {t('syncStartDiscovery')}
        </button>
      </div>
      {refreshJob && <div className="sync-job-progress" role="status">
        <div><strong>{t('syncNewDiscoveryJob')}: {refreshJob.id.slice(0, 8)}</strong>
          <p>{t('syncStatus')}: {refreshJob.state} · {t('syncAttempts')}: {
            refreshJob.attempts}/{refreshJob.maxAttempts}</p>
          {refreshJob.lastErrorCode &&
            <p className="sync-alert-text">{refreshJob.lastErrorCode}</p>}
        </div>
        <button type="button" className="icon-button" disabled={!!busy}
          onClick={() => void inspectRefreshJob()}>
          <RefreshCw size={15} /> {t('syncRefreshJob')}
        </button>
      </div>}

      <SourceSyncCheckpointPanel
        token={token} preparationId={pulse.preparationId}
        planId={pulse.planId} manifestSha256={pulse.manifestSha256}
        isSuperAdmin={isSuperAdmin} />
      <div className="sync-repo-heading">
        <div><h2>{t('syncPerRepository')}</h2>
          <p>{t('syncRepositoryIntro')}</p></div>
        <div className="sync-repo-filters">
          <input aria-label={t('syncFilterRepo')}
            value={search} onChange={e => setSearch(e.target.value)}
            placeholder={t('syncFilterRepo')} />
          <label><input type="checkbox" checked={onlyChanges}
            onChange={e => setOnlyChanges(e.target.checked)} />
            {t('syncOnlyChanges')}</label>
        </div>
      </div>
      <div className="sync-repo-list">
        {visible.map(row =>
          <IsolatedRepository key={row.sourceRepositoryId} row={row}
            onOpenRecovery={() => onOpenRecovery(
              pulse.preparationId, pulse.planId)} />)}
      </div>
      {!visible.length && <div className="empty-card">
        <p>{t('syncNoMatchingRepositories')}</p>
      </div>}
    </>}
  </section>;
}
