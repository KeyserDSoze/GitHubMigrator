import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowDown, GitCompareArrows, RefreshCw, Search, ShieldAlert, ShieldCheck } from 'lucide-react';
import {
  api, type IncrementalPreparation, type IncrementalOperationPlan,
  type GitHubTargetRefEvidence, type GitRefReconciliation, type GitRefComparison
} from './api';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ellipsisSha = (sha: string) => sha.length > 16 ? sha.slice(0, 16) + '…' : sha;
const displayDate = (iso: string) => new Date(iso).toLocaleString();
const errorCode = (e: unknown) => e instanceof Error ? e.message : 'UnknownError';

type Props = {
  token: string; isAdmin: boolean;
  initialPreparationId?: string; initialPlanId?: string;
};

export default function RefReconciliationPage({
  token, isAdmin, initialPreparationId, initialPlanId
}: Props) {
  const { t } = useTranslation();
  const [requestedId, setRequestedId] = useState(initialPreparationId ?? '');
  const [preparation, setPreparation] = useState<IncrementalPreparation | null>(null);
  const [plans, setPlans] = useState<IncrementalOperationPlan[]>([]);
  const [planId, setPlanId] = useState('');
  const [repositoryId, setRepositoryId] = useState('');
  const [targetEvidence, setTargetEvidence] = useState<GitHubTargetRefEvidence[]>([]);
  const [observationId, setObservationId] = useState('');
  const [history, setHistory] = useState<GitRefReconciliation[]>([]);
  const [selectedHistoryId, setSelectedHistoryId] = useState('');
  const [items, setItems] = useState<GitRefComparison[]>([]);
  const [nextAfter, setNextAfter] = useState<string | null>(null);
  const [busy, setBusy] = useState('');
  const [loading, setLoading] = useState(false);
  const [loadingDetails, setLoadingDetails] = useState(false);
  const [error, setError] = useState('');
  const [detailError, setDetailError] = useState('');
  const [notice, setNotice] = useState('');
  const requestId = useRef(0);
  const detailRequestId = useRef(0);

  const activePlan = plans.find(x => x.id === planId) ?? null;
  const mappings = activePlan?.steps.filter(x => x.kind === 'ReviewTargetName') ?? [];
  const selectedMapping = mappings.find(x => x.sourceRepositoryId === repositoryId);
  const availableTargets = targetEvidence.filter(x => x.sourceRepositoryId === repositoryId);
  const selectedTarget = availableTargets.find(x => x.id === observationId);
  const activeHistory = history.filter(x => x.sourceRepositoryId === repositoryId);
  const selectedHistory = activeHistory.find(x => x.id === selectedHistoryId);
  const canCollect = activePlan?.isCurrent && selectedMapping && !busy && !loading;
  const canCompare = canCollect && selectedTarget?.isCurrent &&
    selectedTarget.manifestSha256 === activePlan?.manifestSha256;

  const loadPreparation = useCallback(async (id: string, preferredPlanId = '') => {
    if (!isAdmin || !uuid.test(id.trim())) {
      setError(t('refInvalidPreparation')); return;
    }
    const stamp = ++requestId.current;
    ++detailRequestId.current;
    setLoading(true); setError(''); setNotice(''); setPreparation(null);
    setPlans([]); setPlanId(''); setRepositoryId('');
    setTargetEvidence([]); setHistory([]); setObservationId('');
    setSelectedHistoryId(''); setItems([]); setNextAfter(null);
    try {
      const [run, matches] = await Promise.all([
        api.getIncrementalPreparation(token, id.trim()),
        api.listIncrementalOperationPlans(token, id.trim())
      ]);
      if (stamp !== requestId.current) return;
      setPreparation(run); setPlans(matches);
      setPlanId(matches.some(x => x.id === preferredPlanId)
        ? preferredPlanId : matches[0]?.id ?? '');
    } catch (e) {
      if (stamp === requestId.current) setError(t('refLoadingFailed') + ': ' + errorCode(e));
    } finally {
      if (stamp === requestId.current) setLoading(false);
    }
  }, [isAdmin, t, token]);

  useEffect(() => {
    if (isAdmin && initialPreparationId) {
      setRequestedId(initialPreparationId);
      void loadPreparation(initialPreparationId, initialPlanId);
    }
    // Cleanup blocks earlier requests from repopulating the page after a route change.
    return () => { ++requestId.current; ++detailRequestId.current; };
  }, [initialPreparationId, initialPlanId, isAdmin, loadPreparation]);

  const refresh = useCallback(async (id: string, currentPlanId: string) => {
    const stamp = ++requestId.current;
    ++detailRequestId.current;
    setLoading(true); setError(''); setTargetEvidence([]);
    setHistory([]); setSelectedHistoryId(''); setItems([]); setNextAfter(null);
    try {
      const [plan, targets, comparisons] = await Promise.all([
        api.getIncrementalOperationPlan(token, id, currentPlanId),
        api.listGitHubTargetRefEvidence(token, id, currentPlanId),
        api.listGitRefReconciliations(token, id, currentPlanId)
      ]);
      if (stamp !== requestId.current) return;
      setPlans(prev => prev.map(x => x.id === plan.id ? plan : x));
      setTargetEvidence(targets);
      setHistory(comparisons);
      setObservationId(prev => targets.some(x => x.id === prev) ? prev :
        targets.find(x => x.isCurrent)?.id ?? targets[0]?.id ?? '');
    } catch (e) {
      if (stamp === requestId.current)
        setError(t('refLoadingFailed') + ': ' + errorCode(e));
    } finally {
      if (stamp === requestId.current) setLoading(false);
    }
  }, [t, token]);

  useEffect(() => {
    if (!isAdmin || !preparation || !planId) return;
    const plan = plans.find(x => x.id === planId);
    setRepositoryId(plan?.steps.find(x => x.kind === 'ReviewTargetName')?.sourceRepositoryId ?? '');
    setObservationId(''); setSelectedHistoryId(''); setItems([]); setNextAfter(null);
    void refresh(preparation.id, planId);
    // On unmount or plan switch, invalidate in-flight page/snapshot responses.
    return () => { ++requestId.current; ++detailRequestId.current; };
    // Switching plan is the only trigger: refresh updates the plan object in place.
  }, [isAdmin, preparation?.id, planId, refresh]);

  useEffect(() => {
    ++detailRequestId.current;
    setItems([]); setNextAfter(null); setDetailError('');
    setSelectedHistoryId('');
    setObservationId(targetEvidence.filter(x => x.sourceRepositoryId === repositoryId)
      .find(x => x.isCurrent)?.id ??
      targetEvidence.find(x => x.sourceRepositoryId === repositoryId)?.id ?? '');
  }, [repositoryId]);

  async function capture() {
    if (!preparation || !activePlan?.isCurrent || !selectedMapping || busy) return;
    setBusy('capture'); setError(''); setNotice('');
    try {
      const record = await api.captureGitHubTargetRefEvidence(
        token, preparation.id, activePlan.id,
        activePlan.manifestSha256, selectedMapping.sourceRepositoryId);
      await refresh(preparation.id, activePlan.id);
      setObservationId(record.id);
      setNotice(t('refCaptureSaved'));
    } catch (e) { setError(t('refCaptureFailed') + ': ' + errorCode(e)); }
    finally { setBusy(''); }
  }

  async function loadDetails(id: string, after?: string | null, append = false) {
    if (!preparation || !activePlan || !id) return;
    const stamp = ++detailRequestId.current;
    setSelectedHistoryId(id); setLoadingDetails(true); setDetailError('');
    if (!append) { setItems([]); setNextAfter(null); }
    try {
      const page = await api.getGitRefReconciliationPage(
        token, preparation.id, activePlan.id, id, after, 50);
      if (stamp !== detailRequestId.current) return;
      // Never append a page from a different immutable reconciliation.
      if (page.reconciliation.id !== id)
        throw new Error('ReconciliationIdentityMismatch');
      setItems(prev => append ? [...prev, ...page.items] : page.items);
      setNextAfter(page.nextAfter);
    } catch (e) {
      if (stamp === detailRequestId.current)
        setDetailError(t('refDetailsFailed') + ': ' + errorCode(e));
    } finally {
      if (stamp === detailRequestId.current) setLoadingDetails(false);
    }
  }

  async function compare() {
    if (!preparation || !activePlan?.isCurrent || !selectedMapping ||
      !selectedTarget?.isCurrent || busy) return;
    setBusy('compare'); setError(''); setNotice('');
    try {
      const record = await api.createGitRefReconciliation(
        token, preparation.id, activePlan.id, selectedMapping.sourceRepositoryId,
        selectedTarget.id, activePlan.manifestSha256, selectedTarget.evidenceSha256);
      await refresh(preparation.id, activePlan.id);
      setNotice(t('refComparisonSaved'));
      await loadDetails(record.id);
    } catch (e) { setError(t('refComparisonFailed') + ': ' + errorCode(e)); }
    finally { setBusy(''); }
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    void loadPreparation(requestedId);
  }

  if (!isAdmin) return <section className="empty-card">
    <ShieldAlert size={30} /><h2>{t('refWorkbench')}</h2><p>{t('refRequiresAdmin')}</p>
  </section>;

  return <div className="ref-workbench">
    <section className="ref-warning" role="note">
      <ShieldCheck size={20} /><div><strong>{t('refNoWrites')}</strong>
        <p>{t('refNoWritesDetail')}</p></div>
    </section>
    <form className="ref-controls" onSubmit={submit}>
      <label>{t('refPreparationId')}
        <input value={requestedId} onChange={e => setRequestedId(e.target.value)}
          placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" required
          maxLength={36} autoComplete="off" />
      </label>
      <button className="primary" type="submit"
        disabled={loading || !!busy || !uuid.test(requestedId.trim())}>
        <Search size={16} /> {t('refOpenPreparation')}
      </button>
      {preparation && <button type="button" className="secondary-button"
        disabled={loading || !!busy || !planId} onClick={() => { void refresh(preparation.id, planId); }}>
        <RefreshCw size={16} /> {t('refRefresh')}
      </button>}
    </form>
    {loading && <p className="muted" role="status">{t('refLoading')}</p>}
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    {preparation && <section className="ref-panel">
      <header className="ref-panel-head"><div><div className="eyebrow">{t('refPreparation')}</div>
        <h2>{preparation.id}</h2>
        <p>{t('refPreparationState')}: <strong>{preparation.state}</strong>
          {' · '}{t('refSnapshot')}: <code>{ellipsisSha(preparation.snapshotId)}</code></p></div>
      </header>
      <div className="ref-fields">
        <label>{t('refOperationPlan')}
          <select aria-label={t('refOperationPlan')} value={planId}
            onChange={e => setPlanId(e.target.value)}>
            {plans.length === 0 && <option value="">{t('refNoPlans')}</option>}
            {plans.map(x => <option key={x.id} value={x.id}>
              {displayDate(x.createdAtUtc)} · {ellipsisSha(x.manifestSha256)}
            </option>)}
          </select>
        </label>
        {activePlan && <label>{t('refMapping')}
          <select aria-label={t('refMapping')} value={repositoryId}
            onChange={e => setRepositoryId(e.target.value)}>
            {mappings.map(x => <option value={x.sourceRepositoryId} key={x.sourceRepositoryId}>
              {x.sourceRepositoryId} → {x.targetRepositoryName}
            </option>)}
          </select>
        </label>}
      </div>
      {activePlan && <div className="ref-proof">
        <div><span>{t('refPlanDigest')}</span><code title={activePlan.manifestSha256}>
          {ellipsisSha(activePlan.manifestSha256)}</code></div>
        <div><span>{t('refPlanStatus')}</span><strong>
          {activePlan.isCurrent ? t('refCurrent') : t('refStale')}</strong></div>
        <div><span>{t('refExecution')}</span><strong>{t('refBlocked')}</strong></div>
      </div>}
      {activePlan && !activePlan.isCurrent && <div className="ref-stale" role="note">
        <ShieldAlert size={16} /> {t('refPlanStaleWarning')}</div>}
    </section>}
    {activePlan && selectedMapping && <section className="ref-panel">
      <header className="ref-panel-head">
        <div><div className="eyebrow">F-031 → F-032</div>
          <h2>{t('refCollectTitle')}</h2>
          <p>{t('refCollectIntro')}</p></div>
      </header>
      <div className="ref-action-row">
        <button className="secondary-button" type="button"
          disabled={!canCollect} onClick={() => { void capture(); }}>
          <RefreshCw size={16} /> {busy === 'capture' ? t('refWorking') : t('refCollect')}
        </button>
        <label>{t('refTargetEvidence')}
          <select aria-label={t('refTargetEvidence')} value={observationId}
            onChange={e => setObservationId(e.target.value)}>
            {!availableTargets.length && <option value="">{t('refNoTargetEvidence')}</option>}
            {availableTargets.map(x => <option key={x.id} value={x.id}>
              {displayDate(x.observedAtUtc)} · {x.refCount} {t('refRefs')} ·
              {x.isCurrent ? t('refCurrent') : t('refStale')}
            </option>)}
          </select>
        </label>
        <button className="primary" type="button" disabled={!canCompare}
          onClick={() => { void compare(); }}>
          <GitCompareArrows size={16} /> {busy === 'compare'
            ? t('refWorking') : t('refCompare')}
        </button>
      </div>
      {selectedTarget && <div className="ref-proof">
        <div><span>{t('refTargetDigest')}</span>
          <code title={selectedTarget.evidenceSha256}>{ellipsisSha(selectedTarget.evidenceSha256)}</code></div>
        <div><span>{t('refObservedAt')}</span><strong>{displayDate(selectedTarget.observedAtUtc)}</strong></div>
        <div><span>{t('refTargetRefs')}</span><strong>{selectedTarget.refCount}</strong></div>
      </div>}
      {selectedTarget && !selectedTarget.isCurrent &&
        <div role="note" className="ref-stale"><ShieldAlert size={16} />
          {t('refTargetStaleWarning')}</div>}
    </section>}
    {activePlan && <section className="ref-panel">
      <header className="ref-panel-head"><div><div className="eyebrow">F-032</div>
        <h2>{t('refHistoryTitle')}</h2><p>{t('refHistoryIntro')}</p></div></header>
      {!activeHistory.length
        ? <p className="muted">{t('refNoComparisons')}</p>
        : <div className="ref-history">
          {activeHistory.map(item => <article className="ref-history-card" key={item.id}>
            <div className="ref-history-title"><div><strong>{item.targetRepositoryName}</strong>
              <small>{displayDate(item.createdAtUtc)}</small></div>
              <span className={item.isCurrent ? 'ref-status current' : 'ref-status'}>
                {item.isCurrent ? t('refCurrent') : t('refStale')}</span></div>
            <div className="ref-stats">
              <div><strong>{item.matchingCount}</strong><small>{t('refMatching')}</small></div>
              <div><strong>{item.createCandidateCount}</strong><small>{t('refCandidates')}</small></div>
              <div><strong>{item.divergedCount}</strong><small>{t('refDiverged')}</small></div>
              <div><strong>{item.targetOnlyCount}</strong><small>{t('refPreserved')}</small></div>
            </div>
            <div className="ref-proof ref-history-proof">
              <div><span>{t('refSourceDigest')}</span><code title={item.sourceEvidenceSha256}>
                {ellipsisSha(item.sourceEvidenceSha256)}</code></div>
              <div><span>{t('refDiffDigest')}</span><code title={item.reconciliationSha256}>
                {ellipsisSha(item.reconciliationSha256)}</code></div>
            </div>
            <button className="secondary-button" type="button"
              aria-pressed={selectedHistoryId === item.id} disabled={loadingDetails}
              onClick={() => { void loadDetails(item.id); }}>
              {t('refViewDetails')} <ArrowDown size={15} />
            </button>
          </article>)}
        </div>}
      {selectedHistoryId && <section className="ref-details"
        aria-label={t('refDetailsTitle')}>
        <h3>{t('refDetailsTitle')} · {selectedHistory?.targetRepositoryName ?? ''}</h3>
        {selectedHistory && <p className="muted">{t('refSelectedCount')}: {items.length}
          {' / '}{selectedHistory.matchingCount + selectedHistory.createCandidateCount +
            selectedHistory.divergedCount + selectedHistory.targetOnlyCount}</p>}
        {detailError && <div className="alert" role="alert">{detailError}</div>}
        <div className="ref-table-scroll"><table className="ref-table">
          <thead><tr><th>{t('refRefName')}</th><th>{t('refSourceObject')}</th>
            <th>{t('refTargetObject')}</th><th>{t('refDisposition')}</th>
            <th>{t('refBlocker')}</th></tr></thead>
          <tbody>{items.map(x => <tr key={x.name}>
            <td><code>{x.name}</code></td>
            <td><code title={x.sourceObjectId ?? ''}>{x.sourceObjectId ?
              ellipsisSha(x.sourceObjectId) : '—'}</code></td>
            <td><code title={x.targetObjectId ?? ''}>{x.targetObjectId ?
              ellipsisSha(x.targetObjectId) : '—'}</code></td>
            <td><span className={'ref-kind kind-' + x.disposition}>
              {t('refDisposition' + x.disposition)}</span></td>
            <td><code>{x.blockerCode === 'None' ? '—' : x.blockerCode}</code></td>
          </tr>)}</tbody></table></div>
        {nextAfter && <button type="button" className="secondary-button"
          disabled={loadingDetails} onClick={() => { void loadDetails(selectedHistoryId, nextAfter, true); }}>
          {t('refLoadMore')}
        </button>}
        {loadingDetails && <p className="muted" role="status">{t('refLoading')}</p>}
        {!loadingDetails && !items.length && !detailError && <p className="muted">{t('refNoRefs')}</p>}
      </section>}
    </section>}
  </div>;
}
