import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, ClipboardCopy, RefreshCw, ShieldAlert } from 'lucide-react';
import { api, type Connection, type BoardsPreview, type PipelineBlueprint } from './api';

type Props = { mode: 'pipelines' | 'boards'; token: string; isAdmin: boolean };
export default function WorkloadsPage({ mode, token, isAdmin }: Props) {
  const { t } = useTranslation();
  const [sources, setSources] = useState<Connection[]>([]);
  const [targets, setTargets] = useState<Connection[]>([]);
  const [sourceId, setSourceId] = useState('');
  const [destinationId, setDestinationId] = useState('');
  const [projectId, setProjectId] = useState('');
  const [pipelineId, setPipelineId] = useState('');
  const [repository, setRepository] = useState('');
  const [itemIds, setItemIds] = useState('');
  const [issueMap, setIssueMap] = useState('');
  const [pipeline, setPipeline] = useState<PipelineBlueprint | null>(null);
  const [boards, setBoards] = useState<BoardsPreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    if (!isAdmin) return;
    let active = true;
    api.listConnections(token).then(all => {
      if (!active) return;
      const source = all.filter(x => x.direction === 'source' && x.providerKey === 'azure-devops-services');
      const destination = all.filter(x => x.direction === 'destination' &&
        x.providerKey === 'github-enterprise-cloud' && x.validationCode === 'Verified');
      setSources(source); setTargets(destination);
      if (source.length) setSourceId(source[0].id);
      if (destination.length) setDestinationId(destination[0].id);
    }).catch(() => { if (active) setError(t('workloadError')); });
    return () => { active = false; };
  }, [token, isAdmin, t]);

  async function preview() {
    setError(''); setNotice(''); setPipeline(null); setBoards(null); setBusy(true);
    try {
      if (mode === 'pipelines') {
        const number = Number(pipelineId);
        if (!Number.isSafeInteger(number) || number < 1) throw new Error('InvalidPipelineId');
        setPipeline(await api.previewPipeline(token, sourceId, destinationId, projectId, number, repository));
      } else {
        const ids = itemIds.split(',').map(x => Number(x.trim()));
        if (ids.length < 1 || ids.length > 100 ||
            ids.some(x => !Number.isSafeInteger(x) || x <= 0) || new Set(ids).size !== ids.length)
          throw new Error('InvalidWorkItemIds');
        const known: Record<number, number> = {};
        if (issueMap.trim()) for (const entry of issueMap.split(',')) {
          const parts = entry.split(':').map(x => Number(x.trim()));
          if (parts.length !== 2 || parts.some(x => !Number.isSafeInteger(x) || x <= 0))
            throw new Error('InvalidIssueMapping');
          known[parts[0]] = parts[1];
        }
        setBoards(await api.previewBoards(token, sourceId, projectId, ids, known));
      }
    } catch { setError(t('workloadError')); }
    finally { setBusy(false); }
  }

  async function copy(value: string) {
    try {
      await navigator.clipboard.writeText(value);
      setNotice(t('workloadCopied'));
    } catch { setError(t('workloadError')); }
  }

  if (!isAdmin) return <section className="empty-card">
    <ShieldAlert size={30} /><p>{t('workloadNoAdmin')}</p>
  </section>;

  return <div className="workloads">
    <div className="workload-notice"><ShieldAlert size={16}/>{t('workloadWarning')}</div>
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    <form className="workload-form" onSubmit={e => { e.preventDefault(); void preview(); }}>
      <label>{t('workloadSource')}
        <select required aria-label={t('workloadSource')} value={sourceId}
          onChange={e => setSourceId(e.target.value)}>
          {!sources.length && <option value="">{t('workloadNoSource')}</option>}
          {sources.map(c => <option key={c.id} value={c.id}>{c.name} · {c.organization}</option>)}
        </select>
      </label>
      <label>{t('workloadProject')}
        <input required value={projectId} onChange={e => setProjectId(e.target.value)}
          placeholder="Azure DevOps project ID" />
      </label>
      {mode === 'pipelines' ? <>
        <label>{t('workloadTarget')}
          <select required aria-label={t('workloadTarget')} value={destinationId}
            onChange={e => setDestinationId(e.target.value)}>
            {!targets.length && <option value="">{t('workloadNoTarget')}</option>}
            {targets.map(c => <option key={c.id} value={c.id}>{c.name} · {c.organization}</option>)}
          </select>
        </label>
        <label>{t('workloadRepository')}
          <input required value={repository} onChange={e => setRepository(e.target.value)} placeholder="repository" />
        </label>
        <label>{t('workloadPipelineId')}
          <input type="number" min={1} required value={pipelineId} onChange={e => setPipelineId(e.target.value)} />
        </label>
      </> : <>
        <label>{t('workloadBoardIds')}
          <input required value={itemIds} onChange={e => setItemIds(e.target.value)} placeholder="101,102,103" />
        </label>
        <label>{t('workloadKnownIssues')}
          <input value={issueMap} onChange={e => setIssueMap(e.target.value)} placeholder="101:77,102:78" />
        </label>
      </>}
      <button type="submit" className="primary" disabled={busy || !sourceId ||
        (mode === 'pipelines' && (!destinationId || !pipelineId || !repository))}>
        <RefreshCw size={16}/>{busy ? t('workloadBusy') : t('workloadPreview')}
      </button>
    </form>
    {pipeline && <section className="workload-output">
      <h2>{pipeline.sourceName}</h2>
      <p>{t('workloadPath')}: <code>{pipeline.workflowPath}</code></p>
      <div className="workload-output-heading">
        <h3>{t('workloadDraft')}</h3>
        <button className="icon-button" type="button" onClick={() => { void copy(pipeline.workflowYaml); }}>
          <ClipboardCopy size={15}/>{t('workloadCopy')}
        </button>
      </div>
      <pre>{pipeline.workflowYaml}</pre>
      <h3>{t('workloadSetup')}</h3>
      {pipeline.setupSteps.map((step, i) => <div key={i} className="workload-entry">
        <strong>{step.name} · {step.kind}</strong>
        <span>{step.existsOnGitHub ? t('workloadExisting') : t('workloadMissing')}</span>
        <p>{step.instructions}</p>
      </div>)}
      <h3>{t('workloadAi')} · {pipeline.ai.status}</h3>
      {pipeline.ai.findings.map((line, i) => <p className="workload-finding" key={i}>{line}</p>)}
      <h3>{t('workloadWarnings')}</h3>
      {pipeline.issues.map((issue, i) => <div key={i} className="workload-entry">
        <AlertTriangle size={14}/><strong>{issue.code}</strong><p>{issue.detail}</p>
      </div>)}
    </section>}
    {boards && <section className="workload-output">
      <h2>{t('workloadIssueDrafts')}</h2>
      {boards.issues.map(issue => <article className="workload-entry" key={issue.sourceId}>
        <div className="workload-output-heading">
          <strong>#{issue.sourceId} · {issue.title}</strong>
          <button className="icon-button" type="button" onClick={() => { void copy(issue.body); }}>
            <ClipboardCopy size={14}/>{t('workloadCopy')}
          </button>
        </div>
        <p>{t('workloadState')}: {issue.state}</p>
        <pre>{issue.body}</pre>
        {issue.issues.map((warning, i) => <p key={i} className="workload-finding">
          {warning.code}: {warning.detail}</p>)}
      </article>)}
      <h3>{t('workloadWarnings')}</h3>
      {boards.globalWarnings.map((warning, i) => <p className="workload-finding" key={i}>
        {warning.code}: {warning.detail}</p>)}
    </section>}
  </div>;
}
