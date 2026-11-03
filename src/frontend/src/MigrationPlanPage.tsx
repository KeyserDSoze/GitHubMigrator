import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Eye, ShieldAlert } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary,
  type PlanReview, type PlanSelection } from './api';

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
    setInventory(null); setSelections([]); setReview(null); setError('');
    if (!isAdmin || !snapshotId) return;
    let active = true;
    setLoading(true);
    api.getDiscovery(token, snapshotId).then(item => {
      if (!active) return;
      setInventory(item);
      setSelections(item.snapshot.projects.flatMap(project =>
        project.repositories.map(repo => ({
          sourceRepositoryId: repo.providerId, targetRepositoryName: repo.name,
          strategy: repo.versionControl === 0 ? GIT_MIRROR : ARCHIVE_AND_TIP, include: false
        }))));
    }).catch(() => { if (active) setError(t('planError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, snapshotId, isAdmin, t]);

  function update(sourceId: string, changes: Partial<PlanSelection>) {
    setSelections(previous => previous.map(row =>
      row.sourceRepositoryId === sourceId ? { ...row, ...changes } : row));
    setReview(null);
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
    <div className="notice" role="status">{t('planSafety')}</div>
    {inventory && <section className="user-card">
      <div className="section-header"><h2>{t('planRepositorySelection')}</h2>
        <span>{selectedCount} / {selections.length} {t('planSelected')}</span></div>
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
      <button type="button" className="primary" onClick={() => void preview()}
        disabled={loading || busy || !destinationId || selectedCount === 0}>
        <Eye size={17} />{busy ? t('planChecking') : t('planPreview')}</button>
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
