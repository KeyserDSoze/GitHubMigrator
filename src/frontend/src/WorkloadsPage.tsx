import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, ClipboardCopy, RefreshCw, ShieldAlert } from 'lucide-react';
import { api, type Connection, type BoardsPreview, type PipelineBlueprint, type BoardReconciliationReport } from './api';

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
  const [commitMap, setCommitMap] = useState('');
  const [pullRequestMap, setPullRequestMap] = useState('');
  const [pipeline, setPipeline] = useState<PipelineBlueprint | null>(null);
  const [boards, setBoards] = useState<BoardsPreview | null>(null);
  const [reconciliation, setReconciliation] = useState<BoardReconciliationReport | null>(null);
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

  function parseIds(): number[] {
    const ids = itemIds.split(',').map(x => Number(x.trim()));
    if (ids.length < 1 || ids.length > 100 ||
        ids.some(x => !Number.isSafeInteger(x) || x < 1) || new Set(ids).size !== ids.length)
      throw new Error('InvalidWorkItemIds');
    return ids;
  }
  function parseNumberMap(value: string): Record<number, number> {
    const result: Record<number, number> = {};
    if (value.trim()) for (const part of value.split(',')) {
      const numbers = part.split(':').map(x => Number(x.trim()));
      if (numbers.length !== 2 || numbers.some(x => !Number.isSafeInteger(x) || x < 1))
        throw new Error('InvalidMapping');
      result[numbers[0]] = numbers[1];
    }
    return result;
  }
  function parseCommitMap(value: string): Record<string, string> {
    const result: Record<string, string> = {};
    if (value.trim()) for (const part of value.split(',')) {
      const pair = part.split(':').map(x => x.trim().toLowerCase());
      if (pair.length !== 2 || pair.some(x => !/^[0-9a-f]{40}$/.test(x)))
        throw new Error('InvalidCommitMapping');
      result[pair[0]] = pair[1];
    }
    return result;
  }

  async function verifyLinks() {
    if (!sourceId || !destinationId || !projectId || !repository) return;
    setBusy(true); setError(''); setNotice(''); setReconciliation(null);
    try {
      const result = await api.reconcileBoards(token, sourceId, destinationId, projectId,
        repository, parseIds(), parseNumberMap(issueMap), parseCommitMap(commitMap),
        parseNumberMap(pullRequestMap));
      setReconciliation(result);
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
      <label>{t('workloadTarget')}
          <select required aria-label={t('workloadTarget')} value={destinationId}
            onChange={e => setDestinationId(e.target.value)}>
            {!targets.length && <option value="">{t('workloadNoTarget')}</option>}
            {targets.map(c => <option key={c.id} value={c.id}>{c.name} · {c.organization}</option>)}
          </select>
        </label>
        <label>{t('workloadRepository')}
          <input value={repository} onChange={e => setRepository(e.target.value)} placeholder="repository" />
        </label>
      {mode === 'pipelines' ? <>
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
        <label>{t('workloadCommits')}
          <input value={commitMap} onChange={e => setCommitMap(e.target.value)}
            placeholder="40-char source SHA:40-char GitHub SHA" />
        </label>
        <label>{t('workloadPulls')}
          <input value={pullRequestMap} onChange={e => setPullRequestMap(e.target.value)} placeholder="301:55" />
        </label>
      </>}
      <button type="submit" className="primary" disabled={busy || !sourceId ||
        (mode === 'pipelines' && (!destinationId || !pipelineId || !repository))}>
        <RefreshCw size={16}/>{busy ? t('workloadBusy') : t('workloadPreview')}
      </button>
      {mode === 'boards' && <button type="button" className="icon-button"
        disabled={busy || !sourceId || !destinationId || !projectId || !repository || !itemIds}
        onClick={() => { void verifyLinks(); }}>
        {t('workloadVerify')}
      </button>}
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
    {reconciliation && <section className="workload-output">
      <h2>{t('workloadReconciliation')}</h2>
      <p>{t('workloadTargetObjects')}: <strong>{reconciliation.targetObjectsFound}</strong>
        {' · '}{t('workloadUnresolved')}: <strong>{reconciliation.unresolvedLinks}</strong></p>
      <div className="workload-notice"><ShieldAlert size={16} />{t('workloadExistsNotLinked')}</div>
      {reconciliation.links.map((link, index) => <div className="workload-entry" key={index}>
        <strong>#{link.workItemId} · {link.sourceRelation}</strong>
        <p>{link.sourceReference} → {link.targetReference || '—'}</p>
        <span>{link.status}</span>
      </div>)}
      {reconciliation.issues.map((issue, index) => <p className="workload-finding" key={index}>
        {issue.code}: {issue.detail}</p>)}
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
