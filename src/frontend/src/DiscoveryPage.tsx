import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, RefreshCw, ShieldAlert, Users } from 'lucide-react';
import { api, type Connection, type DiscoveryDetail, type DiscoverySummary, type MigrationJob, type StagingSyncResult, type DiscoveryRefIngestProgress, type DiscoveryRefManifest, type DiscoveryIdentityProgress } from './api';

type Tab = 'projects' | 'users' | 'security' | 'issues';

export default function DiscoveryPage({ token, isAdmin }: { token: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const [sources, setSources] = useState<Connection[]>([]);
  const [sourceId, setSourceId] = useState('');
  const [summaries, setSummaries] = useState<DiscoverySummary[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [detail, setDetail] = useState<DiscoveryDetail | null>(null);
  const [staging, setStaging] = useState<StagingSyncResult | null>(null);
  const [job, setJob] = useState<MigrationJob | null>(null);
  const [refProgress, setRefProgress] = useState<DiscoveryRefIngestProgress | null>(null);
  const [identityProgress, setIdentityProgress] = useState<DiscoveryIdentityProgress | null>(null);
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
    setBusy(true); setError(''); setNotice(''); setRefProgress(null); setIdentityProgress(null);
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
