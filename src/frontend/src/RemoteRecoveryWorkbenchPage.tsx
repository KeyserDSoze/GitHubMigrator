import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AlertCircle, CheckCircle2, Clock3, Database, FileCheck2,
  GitBranch, LockKeyhole, RefreshCw, Search, ShieldAlert, ShieldCheck
} from 'lucide-react';
import {
  api, type IncrementalOperationPlan, type RemoteRecoveryItem,
  type RemoteRecoveryNextAction
} from './api';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const shortHash = (value: string) => value.length > 22 ? value.slice(0, 18) + '…' : value;
const errorCode = (error: unknown) =>
  error instanceof Error ? error.message : 'UnknownError';

type Props = {
  token: string;
  isSuperAdmin: boolean;
  initialPreparationId?: string;
  initialPlanId?: string;
};

function Evidence({ title, value, verified }: {
  title: string; value: string | null | undefined; verified: boolean;
}) {
  return <div className="recovery-evidence">
    <div className="recovery-evidence-label">
      {verified ? <CheckCircle2 size={15} /> : <Clock3 size={15} />}
      <span>{title}</span>
    </div>
    {value ? <code title={value}>{shortHash(value)}</code>
      : <span className="recovery-missing">—</span>}
  </div>;
}

function statusTone(state: RemoteRecoveryItem['state']) {
  if (state === 'OperatorReviewRequired') return 'warning';
  if (state === 'IndependentClosureObserved' ||
      state === 'CheckpointedIndependentClosure') return 'verified';
  return 'pending';
}

function actionKind(next: RemoteRecoveryNextAction) {
  if (next === 'ObserveTipsReadOnly') return 'observe-tips' as const;
  if (next === 'VerifyFullObjectsReadOnly') return 'verify-full-objects' as const;
  if (next === 'CertifyIndependentClosure') return 'closure-checkpoints' as const;
  return null;
}

export default function RemoteRecoveryWorkbenchPage({
  token, isSuperAdmin, initialPreparationId, initialPlanId
}: Props) {
  const { t } = useTranslation();
  const [requestedId, setRequestedId] = useState(initialPreparationId ?? '');
  const [preparationId, setPreparationId] = useState('');
  const [plans, setPlans] = useState<IncrementalOperationPlan[]>([]);
  const [planId, setPlanId] = useState('');
  const [epochs, setEpochs] = useState<RemoteRecoveryItem[]>([]);
  const [nextAfter, setNextAfter] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [busyBarrier, setBusyBarrier] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const requestSequence = useRef(0);

  const loadPage = useCallback(async (
    preparation: string, plan: string, after: string | null = null
  ) => {
    const stamp = ++requestSequence.current;
    setLoading(true); setError('');
    try {
      const result = await api.listRemoteRecoveryWorkbench(
        token, preparation, plan, after);
      if (stamp !== requestSequence.current) return;
      // A bounded server-side cursor is always authoritative. No
      // guesses about success from an empty list or missing proof.
      if (result.canPush || result.canRetry || result.canDispatch ||
          result.migrationComplete || result.items.some(item =>
          item.canPush || item.canRetry || item.canDispatch ||
          item.migrationComplete)) {
        throw new Error('RecoveryWorkbenchUnsafeResponse');
      }
      setEpochs(prev => after ? [...prev, ...result.items] : result.items);
      setNextAfter(result.nextAfter);
    } catch (e) {
      if (stamp === requestSequence.current)
        setError(t('recoveryLoadFailed') + ': ' + errorCode(e));
    } finally {
      if (stamp === requestSequence.current) setLoading(false);
    }
  }, [t, token]);

  const openPreparation = useCallback(async (
    id: string, preferredPlanId = ''
  ) => {
    if (!uuid.test(id.trim())) {
      setError(t('recoveryInvalidPreparation')); return;
    }
    const stamp = ++requestSequence.current;
    setLoading(true); setError(''); setNotice('');
    setPreparationId(''); setPlans([]); setPlanId('');
    setEpochs([]); setNextAfter(null);
    try {
      const available = await api.listIncrementalOperationPlans(
        token, id.trim());
      if (stamp !== requestSequence.current) return;
      const selected = available.find(x => x.id === preferredPlanId) ??
        available[0] ?? null;
      setPreparationId(id.trim()); setPlans(available);
      setPlanId(selected?.id ?? '');
      if (selected) void loadPage(id.trim(), selected.id);
    } catch (e) {
      if (stamp === requestSequence.current)
        setError(t('recoveryLoadFailed') + ': ' + errorCode(e));
    } finally {
      if (stamp === requestSequence.current) setLoading(false);
    }
  }, [loadPage, t, token]);

  useEffect(() => {
    if (isSuperAdmin && initialPreparationId) {
      setRequestedId(initialPreparationId);
      void openPreparation(initialPreparationId, initialPlanId);
    }
    return () => { ++requestSequence.current; };
  }, [initialPreparationId, initialPlanId, isSuperAdmin, openPreparation]);

  const operate = async (epoch: RemoteRecoveryItem) => {
    const action = actionKind(epoch.recommendedNextAction);
    if (!action || busyBarrier || loading || !isSuperAdmin) return;
    if (!window.confirm(t('recoveryActionConfirm'))) return;
    setBusyBarrier(epoch.barrierId);
    setError(''); setNotice('');
    try {
      const outcome = await api.executeIndependentRecoveryAction(
        token, epoch, action);
      if (outcome.migrationComplete || outcome.canRetry || outcome.canPush)
        throw new Error('RecoveryWorkbenchUnsafeResponse');
      setNotice(t('recoveryActionSaved'));
      await loadPage(epoch.preparationId, epoch.planId);
    } catch (e) {
      setError(t('recoveryActionFailed') + ': ' + errorCode(e));
    } finally {
      setBusyBarrier('');
    }
  };

  if (!isSuperAdmin) {
    return <section className="empty-card" role="alert">
      <LockKeyhole size={28} />
      <h2>{t('accessDenied')}</h2>
      <p>{t('recoverySuperAdminOnly')}</p>
    </section>;
  }

  const needsReview = epochs.filter(x =>
    x.recommendedNextAction === 'OperatorReviewRequired').length;
  const awaiting = epochs.filter(x =>
    x.state === 'AwaitingIndependentReadback').length;
  const verified = epochs.filter(x =>
    x.state === 'IndependentClosureObserved' ||
    x.state === 'CheckpointedIndependentClosure').length;

  return <section className="recovery-workbench">
    <div className="recovery-safety">
      <ShieldCheck size={21} />
      <div><strong>{t('recoverySafetyTitle')}</strong>
        <p>{t('recoverySafetyDescription')}</p></div>
      <span className="recovery-mode">READ-ONLY / NO RETRY</span>
    </div>

    <form className="recovery-search" onSubmit={(event: FormEvent) => {
      event.preventDefault(); void openPreparation(requestedId);
    }}>
      <label htmlFor="recovery-preparation-id">{t('recoveryPreparation')}</label>
      <div className="recovery-search-controls">
        <input id="recovery-preparation-id" placeholder="UUID"
          value={requestedId} onChange={event => setRequestedId(event.target.value)}
          aria-invalid={Boolean(error && !uuid.test(requestedId))} />
        <button className="primary" type="submit"
          disabled={loading || !!busyBarrier}>
          <Search size={17} /> {t('recoveryOpen')}
        </button>
      </div>
    </form>

    {preparationId && <div className="recovery-plan-row">
      <label htmlFor="recovery-plan-id">{t('recoveryPlan')}</label>
      <select id="recovery-plan-id" value={planId}
        disabled={loading || !!busyBarrier}
        onChange={event => {
          setPlanId(event.target.value);
          setEpochs([]); setNextAfter(null); setNotice('');
          void loadPage(preparationId, event.target.value);
        }}>
        {plans.map(p => <option key={p.id} value={p.id}>
          {p.organization} · {p.id.slice(0, 8)} · {p.repositoryCount} {t('recoveryRepositories')}
        </option>)}
      </select>
      <button className="icon-button" type="button"
        disabled={!planId || loading || !!busyBarrier}
        onClick={() => void loadPage(preparationId, planId)}>
        <RefreshCw size={16} /> {t('recoveryRefresh')}
      </button>
    </div>}

    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}

    {preparationId && !plans.length && !loading &&
      <p className="muted">{t('recoveryNoPlans')}</p>}

    {planId && <>
      <div className="recovery-stats" aria-label={t('recoveryLoadedResults')}>
        <div><Database size={19} /><strong>{epochs.length}</strong>
          <span>{t('recoveryLoadedResults')}</span></div>
        <div><Clock3 size={19} /><strong>{awaiting}</strong>
          <span>{t('recoveryAwaiting')}</span></div>
        <div><ShieldAlert size={19} /><strong>{needsReview}</strong>
          <span>{t('recoveryReview')}</span></div>
        <div><FileCheck2 size={19} /><strong>{verified}</strong>
          <span>{t('recoveryVerifiedEvidence')}</span></div>
      </div>
      <div className="recovery-section-title">
        <div><h2>{t('recoveryHistory')}</h2>
          <p>{t('recoveryHistoryIntro')}</p></div>
      </div>
      {!loading && !epochs.length &&
        <div className="empty-card"><Database size={26} />
          <p>{t('recoveryEmpty')}</p></div>}
      <div className="recovery-epochs">
        {epochs.map(epoch => <article key={epoch.barrierId}
          className={'recovery-epoch recovery-' + statusTone(epoch.state)}>
          <header className="recovery-epoch-header">
            <div className="recovery-repo">
              <span className="recovery-repo-icon"><GitBranch size={18} /></span>
              <div><strong>{epoch.organization}/{epoch.repository}</strong>
                <small>{t('recoveryMarkerTime')}: {new Date(
                  epoch.possibleSendRecordedAtUtc).toLocaleString()}</small></div>
            </div>
            <span className={'recovery-state recovery-state-' +
              statusTone(epoch.state)}>
              {epoch.state === 'OperatorReviewRequired'
                ? <AlertCircle size={15} /> : epoch.state ===
                  'IndependentClosureObserved' || epoch.state ===
                  'CheckpointedIndependentClosure'
                    ? <CheckCircle2 size={15} /> : <Clock3 size={15} />}
              {t('recoveryState_' + epoch.state)}
            </span>
          </header>
          <div className="recovery-meta">
            <span>{t('recoveryRepositoryId')}: <strong>{epoch.repositoryId}</strong></span>
            <span>{t('recoveryCandidates')}: <strong>{
              epoch.typedBaselineAvailable
                ? epoch.createCandidateCount : '—'}</strong></span>
            <span>{t('recoveryBaselineRefs')}: <strong>{
              epoch.typedBaselineAvailable
                ? epoch.baselineRefCount : '—'}</strong></span>
          </div>
          {!epoch.typedBaselineAvailable &&
            <p className="recovery-warning" role="status">
              <ShieldAlert size={16} /> {t('recoveryMissingBaseline')}
            </p>}
          {epoch.state === 'OperatorReviewRequired' &&
            <p className="recovery-warning" role="status">
              <AlertCircle size={16} /> {t('recoveryMismatchWarning')}
            </p>}
          <div className="recovery-proof-grid">
            <Evidence title="F-082 · Possible send"
              value={epoch.barrierEvidenceSha256} verified />
            <Evidence title="F-086 · Original refs"
              value={epoch.typedBaselineEvidenceSha256}
              verified={epoch.typedBaselineAvailable} />
            <Evidence title="F-054 · Remote tips"
              value={epoch.readbackEvidenceSha256}
              verified={!!epoch.readbackId} />
            <Evidence title="F-058 · Full objects"
              value={epoch.fullObjectClosureSha256}
              verified={!!epoch.reconciliationId} />
            <Evidence title="F-085 · Checkpoint"
              value={epoch.closureCheckpointEvidenceSha256}
              verified={!!epoch.closureCheckpointId} />
          </div>
          <footer className="recovery-epoch-footer">
            <span><LockKeyhole size={15} /> {t('recoveryNeverRetry')}</span>
            {actionKind(epoch.recommendedNextAction) &&
              <button type="button" className="primary"
                disabled={loading || !!busyBarrier}
                onClick={() => void operate(epoch)}>
                <ShieldCheck size={16} />
                {busyBarrier === epoch.barrierId ? t('recoveryWorking') :
                  t('recoveryAction_' + epoch.recommendedNextAction)}
              </button>}
            {epoch.recommendedNextAction === 'OperatorReviewRequired' &&
              <strong className="recovery-needs-review">
                {t('recoveryManualReview')}</strong>}
          </footer>
        </article>)}
      </div>
      {nextAfter && <button className="icon-button recovery-more"
        type="button" disabled={loading || !!busyBarrier}
        onClick={() => void loadPage(preparationId, planId, nextAfter)}>
        <RefreshCw size={16} /> {t('recoveryMore')}
      </button>}
      {loading && <p className="muted" role="status">{t('recoveryLoading')}</p>}
    </>}
  </section>;
}
