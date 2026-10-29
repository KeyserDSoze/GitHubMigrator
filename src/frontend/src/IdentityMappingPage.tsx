import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, RefreshCw, Save, ShieldAlert } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary,
  type IdentityMap, type IdentityReview } from './api';

const validLogin = (value: string) =>
  /^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$/.test(value) && !value.includes('--');

export default function IdentityMappingPage({ token, isAdmin }: { token: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const [snapshots, setSnapshots] = useState<DiscoverySummary[]>([]);
  const [destinations, setDestinations] = useState<Connection[]>([]);
  const [snapshotId, setSnapshotId] = useState('');
  const [destinationId, setDestinationId] = useState('');
  const [detail, setDetail] = useState<DiscoveryDetail | null>(null);
  const [review, setReview] = useState<IdentityReview | null>(null);
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [filter, setFilter] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    let active = true;
    Promise.all([api.listDiscoveries(token), api.listConnections(token)])
      .then(([history, connections]) => {
        if (!active) return;
        setSnapshots(history);
        setDestinations(connections.filter(c =>
          c.providerKey === 'github-enterprise-cloud' && c.direction === 'destination' && c.validationCode === 'Verified'));
        if (history.length) setSnapshotId(history[0].id);
        const targets = connections.filter(c =>
          c.providerKey === 'github-enterprise-cloud' && c.direction === 'destination' && c.validationCode === 'Verified');
        if (targets.length) setDestinationId(targets[0].id);
      }).catch(() => { if (active) setError(t('mappingError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [isAdmin, token, t]);

  useEffect(() => {
    setDetail(null); setReview(null); setEdits({}); setDirty(false); setNotice('');
    if (!snapshotId || !destinationId || !isAdmin) return;
    let active = true;
    setLoading(true);
    Promise.all([api.getDiscovery(token, snapshotId), api.getIdentityReview(token, snapshotId, destinationId)])
      .then(([snapshot, result]) => {
        if (!active) return;
        setDetail(snapshot); setReview(result);
        setEdits(Object.fromEntries(result.draft.mappings.map(x => [x.sourceIdentityId, x.gitHubLogin])));
      })
      .catch(() => { if (active) setError(t('mappingError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [snapshotId, destinationId, token, isAdmin, t]);

  const people = useMemo(() => detail?.snapshot.identities ?? [], [detail]);
  const displayed = useMemo(() => people.filter(user =>
    [user.displayName, user.email || '', user.providerId].some(part =>
      part.toLowerCase().includes(filter.toLowerCase().trim()))).slice(0, 250), [people, filter]);

  function updateMapping(sourceId: string, login: string) {
    setEdits(previous => ({ ...previous, [sourceId]: login }));
    setDirty(true); setNotice('');
  }

  async function save() {
    if (!snapshotId || !destinationId || !review || !detail) return;
    const mappings: IdentityMap[] = people.map(user => ({
      sourceIdentityId: user.providerId, gitHubLogin: (edits[user.providerId] || '').trim()
    })).filter(m => m.gitHubLogin.length > 0);
    const logins = mappings.map(m => m.gitHubLogin.toLowerCase());
    if (mappings.some(m => !validLogin(m.gitHubLogin)) || new Set(logins).size !== logins.length) {
      setError(t('mappingInvalid')); return;
    }
    setBusy(true); setError(''); setNotice('');
    try {
      const result = await api.saveIdentityMappings(token, snapshotId, destinationId,
        review.draft.revision, mappings);
      setReview(result); setDirty(false);
      setEdits(Object.fromEntries(result.draft.mappings.map(x => [x.sourceIdentityId, x.gitHubLogin])));
      setNotice(t('mappingSaved'));
    } catch (e) {
      setError(e instanceof Error && e.message === 'RevisionConflict' ? t('mappingConflict') : t('mappingError'));
    } finally { setBusy(false); }
  }

  async function reload() {
    if (!snapshotId || !destinationId) return;
    setLoading(true); setError(''); setNotice('');
    try {
      const result = await api.getIdentityReview(token, snapshotId, destinationId);
      setReview(result); setEdits(Object.fromEntries(result.draft.mappings.map(x => [x.sourceIdentityId, x.gitHubLogin])));
      setDirty(false);
    } catch { setError(t('mappingError')); } finally { setLoading(false); }
  }

  if (!isAdmin) return <section className="empty-card"><ShieldAlert size={30} /><p>{t('mappingNoAdmin')}</p></section>;
  const currentlyMapped = people.filter(p => (edits[p.providerId] || '').trim() !== '').length;
  const issueNames: Record<string, string> = {
    SourceIdentitiesIncomplete: t('mappingIssueSourceIdentitiesIncomplete'),
    EffectivePermissionsUnverified: t('mappingIssueEffectivePermissionsUnverified'),
    TargetMembershipUnverified: t('mappingIssueTargetMembershipUnverified'),
    UnmappedSourceIdentity: t('mappingIssueUnmappedSourceIdentity'),
    AclPrincipalUnresolved: t('mappingIssueAclPrincipalUnresolved')
  };
  return <div>
    <section className="mapping-selectors">
      <label>{t('mappingSnapshot')}
        <select value={snapshotId} onChange={e => { setSnapshotId(e.target.value); setError(''); }}>
          {snapshots.length === 0 && <option value="">{t('mappingNoSnapshot')}</option>}
          {snapshots.map(s => <option key={s.id} value={s.id}>{s.organization} · {new Date(s.collectedAtUtc).toLocaleString()}</option>)}
        </select>
      </label>
      <label>{t('mappingDestination')}
        <select value={destinationId} onChange={e => { setDestinationId(e.target.value); setError(''); }}>
          {destinations.length === 0 && <option value="">{t('mappingNoDestination')}</option>}
          {destinations.map(d => <option value={d.id} key={d.id}>{d.name} · {d.organization}</option>)}
        </select>
      </label>
    </section>
    <p className="mapping-tip">{t('mappingScope')}</p>
    {error && <div role="alert" className="alert">{error}</div>}
    {notice && <div role="status" className="notice">{notice}</div>}
    {!snapshotId || !destinationId ? <section className="empty-card"><p>{t('mappingNotFound')}</p></section> : <>
      {review && <div className="mapping-summary">
        {[
          [t('mappingDiscoverCount'), people.length],
          [t('mappingMappedCount'), currentlyMapped],
          [t('mappingMissingCount'), Math.max(0, people.length - currentlyMapped)]
        ].map(([title, number]) => <div key={title} className="mapping-metric">
          <strong>{number}</strong><span>{title}</span>
        </div>)}
      </div>}
      {loading && <p className="muted">{t('mappingLoading')}</p>}
      {!loading && detail && review && <div className="mapping-panels">
        <section className="mapping-panel">
          <h2>{t('identities')}</h2>
          <div className="mapping-toolbar">
            <input aria-label={t('mappingSearch')} value={filter}
              onChange={e => setFilter(e.target.value)} placeholder={t('mappingSearch')} />
            <button className="icon-button" onClick={() => { void reload(); }} type="button" disabled={busy}>
              <RefreshCw size={15} />{t('mappingReload')}
            </button>
          </div>
          {people.length === 0 && <p className="muted">{t('mappingNoIdentities')}</p>}
          {displayed.map(person => <div className="mapping-user" key={person.providerId}>
            <div><strong>{person.displayName}</strong>
              <small>{person.email || person.providerId}</small>
              <small>{person.providerId}</small>
            </div>
            <label>{t('connDestination')}
              <input type="text" aria-label={`${person.displayName} GitHub`}
                className={(edits[person.providerId] || '').trim() &&
                  !validLogin((edits[person.providerId] || '').trim()) ? 'invalid' : ''}
                autoComplete="off" spellCheck={false} maxLength={80}
                value={edits[person.providerId] || ''} placeholder={t('mappingPlaceholder')}
                onChange={e => updateMapping(person.providerId, e.target.value)} />
            </label>
          </div>)}
          {people.length > 250 && <p className="mapping-tip">{t('mappingLimit')}</p>}
          <div className="mapping-actions">
            <span>{t('mappingRevision')}: {review.draft.revision}{dirty ? ` · ${t('mappingUnsaved')}` : ''}</span>
            <button type="button" className="primary" disabled={busy || !dirty}
              onClick={() => { void save(); }}><Save size={16} />{busy ? t('mappingSaving') : t('mappingSave')}</button>
          </div>
        </section>
        <aside className="mapping-panel">
          <h2>{t('mappingReview')}</h2>
          <div className="mapping-blocked"><AlertTriangle size={19} /><div>
            <strong>{t('mappingBlocked')}</strong><p>{t('mappingWarning')}</p></div></div>
          <p className="mapping-tip">{t('mappingUnmapped')}: {review.preview.unmappedIdentities} · {t('mappingRevision')}: {review.draft.revision}</p>
          {review.preview.issues.filter(x => x.code !== 'UnmappedSourceIdentity').slice(0, 30)
            .map((issue, i) => <div className="mapping-issue" key={i}>
              <strong>{issueNames[issue.code] || issue.code}</strong>
              {issue.sourceIdentityId && <span>{issue.sourceIdentityId}</span>}
            </div>)}
        </aside>
      </div>}
    </>}
  </div>;
}
