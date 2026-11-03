import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Eye, RefreshCw, Save, ShieldAlert } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary,
  type MigrationTargetPreflight, type PlanReview, type PlanSelection } from './api';

// HistoryStrategy is serialized as a numeric enum by the ASP.NET API.
const GIT_MIRROR = 0;
const TIP_ONLY = 1;
const TFVC_FULL = 2;
const ARCHIVE_AND_TIP = 3;

export default function MigrationPlanPage({ token, isAdmin }: { token: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const [snapshots, setSnapshots] = useState<DiscoverySummary[]>([]);
  const [destinations, setDestinations] = useState<Connection[]>([]);
  const [snapshotId, setSnapshotId] = useState('');
  const [destinationId, setDestinationId] = useState('');
  const [inventory, setInventory] = useState<DiscoveryDetail | null>(null);
  const [selections, setSelections] = useState<PlanSelection[]>([]);
  const [review, setReview] = useState<PlanReview | null>(null);
  const [targetPreflight, setTargetPreflight] = useState<MigrationTargetPreflight | null>(null);
  const [revision, setRevision] = useState(0);
  const [dirty, setDirty] = useState(false);
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    let active = true;
    Promise.all([api.listDiscoveries(token), api.listConnections(token)])
      .then(([items, connections]) => {
        if (!active) return;
        setSnapshots(items);
        const targets = connections.filter(connection =>
          connection.direction === 'destination' &&
          connection.providerKey === 'github-enterprise-cloud' &&
          connection.validationCode === 'Verified');
        setDestinations(targets);
        setSnapshotId(items[0]?.id ?? '');
        setDestinationId(targets[0]?.id ?? '');
      })
      .catch(() => { if (active) setError(t('planError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, isAdmin, t]);

  useEffect(() => {
    setInventory(null); setSelections([]); setReview(null); setTargetPreflight(null); setError('');
    setRevision(0); setDirty(false); setNotice('');
    if (!isAdmin || !snapshotId || !destinationId) return;
    let active = true;
    setLoading(true);
    Promise.all([api.getDiscovery(token, snapshotId),
      api.getMigrationPlanDraft(token, snapshotId, destinationId)])
      .then(([item, draft]) => {
        if (!active) return;
        setInventory(item);
        setRevision(draft.revision);
        const saved = new Map(draft.repositories.map(row => [row.sourceRepositoryId, row]));
        setSelections(item.snapshot.projects.flatMap(project =>
          project.repositories.map(repo => saved.get(repo.providerId) ?? ({
            sourceRepositoryId: repo.providerId, targetRepositoryName: repo.name,
            strategy: repo.versionControl === 0 ? GIT_MIRROR : ARCHIVE_AND_TIP, include: false
          }))));
      }).catch(() => { if (active) setError(t('planError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, snapshotId, destinationId, isAdmin, t]);

  function update(sourceId: string, changes: Partial<PlanSelection>) {
    setSelections(previous => previous.map(row =>
      row.sourceRepositoryId === sourceId ? { ...row, ...changes } : row));
    setDirty(true); setNotice(''); setReview(null); setTargetPreflight(null);
  }

  async function saveDraft() {
    if (!snapshotId || !destinationId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const draft = await api.saveMigrationPlanDraft(token, snapshotId, destinationId,
        revision, selections);
      setRevision(draft.revision);
      setDirty(false);
      setNotice(t('planSaved'));
    } catch (e) {
      setError(e instanceof Error && e.message === 'PlanRevisionConflict'
        ? t('planConflict') : t('planError'));
    } finally { setBusy(false); }
  }

  async function reloadDraft() {
    if (!snapshotId || !destinationId || !inventory) return;
    setBusy(true); setError(''); setNotice(''); setReview(null); setTargetPreflight(null);
    try {
      const draft = await api.getMigrationPlanDraft(token, snapshotId, destinationId);
      const saved = new Map(draft.repositories.map(row => [row.sourceRepositoryId, row]));
      setSelections(inventory.snapshot.projects.flatMap(project =>
        project.repositories.map(repo => saved.get(repo.providerId) ?? ({
          sourceRepositoryId: repo.providerId, targetRepositoryName: repo.name,
          strategy: repo.versionControl === 0 ? GIT_MIRROR : ARCHIVE_AND_TIP, include: false
        }))));
      setRevision(draft.revision); setDirty(false);
    } catch { setError(t('planError')); } finally { setBusy(false); }
  }

  async function preview() {
    if (!inventory || !snapshotId || !destinationId) return;
    setBusy(true); setError(''); setReview(null);
    try {
      setReview(await api.previewMigrationPlan(token, snapshotId, destinationId, selections));
    } catch {
      setError(t('planError'));
    } finally {
      setBusy(false);
    }
  }

  async function checkTarget() {
    if (!inventory || !snapshotId || !destinationId) return;
    setBusy(true); setError(''); setTargetPreflight(null);
    try {
      setTargetPreflight(await api.preflightMigrationPlanTargets(token, snapshotId, destinationId, selections));
    } catch {
      setError(t('planTargetPreflightError'));
    } finally { setBusy(false); }
  }

  if (!isAdmin) return <section className="empty-card"><ShieldAlert size={30} /><p>{t('planNoAdmin')}</p></section>;

  const selectedCount = selections.filter(row => row.include).length;
  const nameFor = new Map(inventory?.snapshot.projects.flatMap(p =>
    p.repositories.map(r => [r.providerId, r.name] as const)) ?? []);
  const blockers = review?.checks.filter(c => c.severity === 'blocker').length ?? 0;

  return <div>
    <section className="mapping-selectors">
      <label>{t('mappingSnapshot')}
        <select value={snapshotId} onChange={event => setSnapshotId(event.target.value)}>
          {!snapshots.length && <option value="">{t('mappingNoSnapshot')}</option>}
          {snapshots.map(item => <option key={item.id} value={item.id}>
            {item.organization} · {new Date(item.collectedAtUtc).toLocaleString()}</option>)}
        </select>
      </label>
      <label>{t('mappingDestination')}
        <select value={destinationId} onChange={event => { setDestinationId(event.target.value); setReview(null); }}>
          {!destinations.length && <option value="">{t('mappingNoDestination')}</option>}
          {destinations.map(item => <option value={item.id} key={item.id}>{item.name} · {item.organization}</option>)}
        </select>
      </label>
    </section>
    {loading && <p className="muted">{t('planLoading')}</p>}
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    <div className="notice" role="status">{t('planSafety')}</div>
    {inventory && <section className="user-card">
      <div className="section-header"><h2>{t('planRepositorySelection')}</h2>
        <span>{selectedCount} / {selections.length} {t('planSelected')} · {t('planRevision')} {revision}{dirty ? ` · ${t('planUnsaved')}` : ''}</span></div>
      <div className="plan-table-wrap">
        <table className="plan-table">
          <thead><tr><th>{t('planInclude')}</th><th>{t('planSource')}</th>
            <th>{t('planTarget')}</th><th>{t('planStrategy')}</th></tr></thead>
          <tbody>{inventory.snapshot.projects.flatMap(project =>
            project.repositories.map(repo => {
              const row = selections.find(s => s.sourceRepositoryId === repo.providerId);
              if (!row) return null;
              return <tr key={repo.providerId}>
                <td><input type="checkbox" checked={row.include} aria-label={t('planInclude') + ': ' + repo.name}
                  onChange={event => update(repo.providerId, { include: event.target.checked })} /></td>
                <td><strong>{repo.name}</strong><small>{project.name} · {repo.versionControl === 0 ? 'Git' : 'TFVC'}</small></td>
                <td><input aria-label={t('planTarget') + ': ' + repo.name} value={row.targetRepositoryName}
                  maxLength={100} onChange={event => update(repo.providerId, { targetRepositoryName: event.target.value })} /></td>
                <td><select aria-label={t('planStrategy') + ': ' + repo.name} value={row.strategy}
                  onChange={event => update(repo.providerId, { strategy: Number(event.target.value) })}>
                  {repo.versionControl === 0
                    ? <><option value={GIT_MIRROR}>Git mirror</option><option value={TIP_ONLY}>Tip only</option></>
                    : <><option value={TFVC_FULL}>Full history (best effort)</option>
                      <option value={ARCHIVE_AND_TIP}>Archive and tip</option></>}
                </select></td>
              </tr>;
            }))}</tbody>
        </table>
      </div>
      <div className="plan-action-row">
        <button type="button" className="primary" onClick={() => void saveDraft()}
          disabled={loading || busy || !destinationId || !dirty}>
          <Save size={17} />{t('planSave')}</button>
        <button type="button" className="plan-secondary" onClick={() => void reloadDraft()}
          disabled={loading || busy || !destinationId}><RefreshCw size={16} />{t('planReload')}</button>
        <button type="button" className="plan-secondary" onClick={() => void preview()}
          disabled={loading || busy || !destinationId || selectedCount === 0}>
          <Eye size={17} />{t('planPreview')}</button>
        <button type="button" className="plan-secondary" onClick={() => void checkTarget()}
          disabled={loading || busy || !destinationId || selectedCount === 0 || selectedCount > 10}>
          <ShieldAlert size={17} />{t('planTargetPreflight')}</button>
      </div>
    </section>}

    {targetPreflight && <section className="user-card" aria-live="polite">
      <div className="section-header"><h2>{t('planTargetResults')}</h2>
        <span>{targetPreflight.checkedCount} {t('planTargetChecked')}</span></div>
      <div className="notice">{t('planTargetCaution')}</div>
      {targetPreflight.repositories.map(result => <div className="plan-check" key={result.sourceRepositoryId}>
        <strong>{result.targetRepositoryName}</strong>
        <span>{t('planTargetStatus' + result.status, { defaultValue: result.status })}</span>
      </div>)}
    </section>}
    {review && <section className="user-card" aria-live="polite">
      <div className="section-header"><h2>{t('planResults')}</h2>
        <span>{review.selectedRepositories} {t('planSelected')} · {blockers} {t('planBlockers')}</span></div>
      <div className="alert"><AlertTriangle size={16} /> {t('planNotApproved')}</div>
      <div className="plan-results">
        {review.repositories.map(row => <div className="plan-result" key={row.sourceRepositoryId}>
          <strong>{nameFor.get(row.sourceRepositoryId) ?? row.sourceName} → {row.targetRepositoryName}</strong>
          <small>{row.refCount} refs · {row.candidateGitHubLogins.length} {t('planCandidateLogins')}</small>
        </div>)}
      </div>
      <h3>{t('planChecks')}</h3>
      {review.checks.map((check, index) => <div className="plan-check" key={index}>
        <span className={check.severity === 'blocker' ? 'plan-blocker' : 'plan-warning'}>
          {check.severity === 'blocker' ? t('planBlocker') : t('planWarning')}</span>
        <span>{t('planIssue' + check.code, { defaultValue: check.code })}</span>
        {check.repositoryId && <small>({nameFor.get(check.repositoryId) ?? check.repositoryId})</small>}
      </div>)}
    </section>}
  </div>;
}
