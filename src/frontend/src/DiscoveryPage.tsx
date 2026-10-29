import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, RefreshCw, ShieldAlert, Users } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary } from './api';

type Tab = 'projects' | 'users' | 'security' | 'issues';

export default function DiscoveryPage({ token, isAdmin }: { token: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const [sources, setSources] = useState<Connection[]>([]);
  const [sourceId, setSourceId] = useState('');
  const [summaries, setSummaries] = useState<DiscoverySummary[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [detail, setDetail] = useState<DiscoveryDetail | null>(null);
  const [tab, setTab] = useState<Tab>('projects');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    let active = true;
    Promise.all([api.listConnections(token), api.listDiscoveries(token)])
      .then(([connections, history]) => {
        if (!active) return;
        const available = connections.filter(c =>
          c.providerKey === 'azure-devops-services' && c.direction === 'source');
        setSources(available);
        if (available.length) setSourceId(available[0].id);
        setSummaries(history);
        if (history.length) {
          setSelectedId(history[0].id);
          void api.getDiscovery(token, history[0].id).then(value => { if (active) setDetail(value); })
            .catch(() => { if (active) setError(t('operationError')); });
        }
      }).catch(() => { if (active) setError(t('operationError')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [token, isAdmin, t]);

  async function viewSnapshot(id: string) {
    setSelectedId(id); setDetail(null); setTab('projects'); setError('');
    try { setDetail(await api.getDiscovery(token, id)); }
    catch { setError(t('operationError')); }
  }

  async function scan() {
    if (!sourceId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const saved = await api.scanDiscovery(token, sourceId);
      setSummaries(current => [saved, ...current.filter(x => x.id !== saved.id)]);
      await viewSnapshot(saved.id);
      setNotice(t('discoverySummary'));
    } catch { setError(t('discoveryScanError')); }
    finally { setBusy(false); }
  }

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
        <RefreshCw size={16} /> {busy ? t('discoveryBusy') : t('discoveryStart')}
      </button>
    </section>
    <div className="discovery-note">{t('discoveryNotReady')}</div>
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
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
                    <small>{repo.sourcePath || `${repoRefs.length} ${t('discoveryBranchCount')}`}</small></div>
                  <span className="kind">{repo.versionControl === 1 ? t('discoveryTfvc') : t('discoveryGit')}</span>
                </div>;
              })}
            </article>) : <p className="muted">{t('discoveryNoProjects')}</p>)}
          {tab === 'users' && <>
            {!detail.identitiesComplete && <div className="discovery-warning"><ShieldAlert size={16} />{t('discoveryGraphIncomplete')}</div>}
            {detail.snapshot.identities.length ? detail.snapshot.identities.map(identity =>
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
            {groups.length === 0 && <p className="muted">{t('discoveryNoSecurity')}</p>}
            {groups.slice(0, 100).map(group => <div className="discovery-security-group" key={group.descriptor}>
              <strong>{group.displayName}</strong>
              <small>{group.legacyDescriptor || group.descriptor}</small>
              <span>{memberships.filter(x => x.groupDescriptor === group.descriptor).length} {t('discoveryMemberships').toLowerCase()}</span>
              <ul>{memberships.filter(x => x.groupDescriptor === group.descriptor).slice(0, 30).map(edge =>
                <li key={edge.memberDescriptor}>{displayMember(edge.memberDescriptor)}</li>)}</ul>
            </div>)}
            {groups.length > 100 && <p className="muted">{t('discoveryListCapped')}</p>}
            <h3>{t('discoveryAcls')}</h3>
            {acls.length === 0 && <p className="muted">{t('discoveryNoSecurity')}</p>}
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
