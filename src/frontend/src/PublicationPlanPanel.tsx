import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, CheckCircle2, ClipboardCopy, Download, GitPullRequest, ShieldAlert } from 'lucide-react';
import { api, ApiError, type Connection, type Publication, type PublicationPlan, type PublicationStatus } from './api';

type Props = { token: string; proposalId: string; destinations: Connection[]; defaultDestinationId: string; defaultRepository: string; isSuperAdmin: boolean };

// For an APPROVED proposal: reads the destination repository and shows what to put where, the words of the pull request and what is in
// the way. It writes nothing. The push and the pull request are made by a person, with the text and commands shown here.
export default function PublicationPlanPanel({ token, proposalId, destinations, defaultDestinationId, defaultRepository, isSuperAdmin }: Props) {
  const { t } = useTranslation();
  const [destinationId, setDestinationId] = useState(defaultDestinationId || destinations[0]?.id || '');
  const [repository, setRepository] = useState(defaultRepository);
  const [baseBranch, setBaseBranch] = useState('');
  const [plan, setPlan] = useState<PublicationPlan | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState('');
  const [status, setStatus] = useState<PublicationStatus | null>(null);
  const [attempts, setAttempts] = useState<Publication[]>([]);
  const [confirmed, setConfirmed] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [publishError, setPublishError] = useState('');
  const [result, setResult] = useState<Publication | null>(null);

  // Whether the product may write at all, and what it has already done for this proposal. Both are context: when they cannot be loaded the plan still works.
  useEffect(() => {
    let active = true;
    api.publicationStatus(token).then(x => { if (active) setStatus(x); }).catch(() => { if (active) setStatus(null); });
    api.listPublications(token, proposalId).then(x => { if (active) setAttempts(x); }).catch(() => { if (active) setAttempts([]); });
    return () => { active = false; };
  }, [token, proposalId]);

  async function publish(p: PublicationPlan) {
    setPublishing(true); setPublishError(''); setResult(null);
    try {
      setResult(await api.publishProposal(token, proposalId, destinationId, p.repository, baseBranch.trim(), p.planSha256));
      setConfirmed(false);
    } catch (e) {
      const code = e instanceof Error ? e.message : '';
      const record = e instanceof ApiError ? (e.body?.publication as Publication | undefined) : undefined;
      const details = e instanceof ApiError ? e.details : [];
      if (record) setResult(record);
      // what the person confirmed is no longer true: the plan is shown again, not edited in place
      if (code === 'PublicationPlanChanged') { setPlan(null); setConfirmed(false); }
      setPublishError(t('pubError_' + code, { defaultValue: t('pubPublishFailed', { code }) }) + (details.length ? ` (${details.join(', ')})` : ''));
    } finally {
      setPublishing(false);
      api.listPublications(token, proposalId).then(setAttempts).catch(() => undefined);
    }
  }

  async function run() {
    setBusy(true); setError(''); setPlan(null); setCopied(''); setConfirmed(false); setResult(null); setPublishError('');
    try { setPlan(await api.planPublication(token, proposalId, destinationId, repository.trim(), baseBranch.trim())); }
    catch (e) {
      const code = e instanceof ApiError || e instanceof Error ? e.message : '';
      setError(t('pubError_' + code, { defaultValue: t('pubErrorGeneric') }));
    } finally { setBusy(false); }
  }

  async function copy(key: string, text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(key); } catch { setError(t('pubErrorGeneric')); }
  }

  function download(p: PublicationPlan) {
    const url = URL.createObjectURL(new Blob([p.workflowYaml], { type: 'text/yaml' }));
    const link = document.createElement('a');
    link.href = url; link.download = p.workflowPath.split('/').pop() ?? 'workflow.yml';
    link.click();
    URL.revokeObjectURL(url);
  }

  const blockers = plan?.findings.filter(f => f.severity === 'Blocker') ?? [];
  const copyButton = (key: string, text: string, label?: string) => <button className="icon-button" type="button" onClick={() => { void copy(key, text); }}>
    <ClipboardCopy size={14} /> {copied === key ? t('workloadCopied') : (label ?? t('workloadCopy'))}
  </button>;


  const allowed = (p: PublicationPlan) => !!status?.allowedRepositories.some(r => r.toLowerCase() === `${p.organization}/${p.repository}`.toLowerCase());
  const linkOf = (r: Publication) => r.pullUrl && /^https?:\/\//.test(r.pullUrl) ? r.pullUrl : null;

  const publishSection = (p: PublicationPlan) => <div className="publication-publish">
    <h5>{t('pubPublishTitle')}</h5>
    {!status?.enabled && <p className="connection-hint">{t('pubPublishDisabled')}</p>}
    {status?.enabled && !allowed(p) && <p className="connection-hint">{t('pubNotAllowed')}</p>}
    {status?.enabled && allowed(p) && !p.ready && <p className="connection-hint">{t('pubNotReady')}</p>}
    {status?.enabled && allowed(p) && p.ready && !isSuperAdmin && <p className="connection-hint">{t('pubSuperadminOnly')}</p>}
    {status?.enabled && allowed(p) && p.ready && isSuperAdmin && !result && <>
      <label className="proposal-warning">
        <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
        <span>{t('pubConfirm', { hash: p.planSha256.slice(0, 12) })}</span>
      </label>
      <div className="proposal-actions">
        <button type="button" className="primary" disabled={!confirmed || publishing} onClick={() => { void publish(p); }}>
          <GitPullRequest size={15} /> {publishing ? t('pubPublishing') : t('pubPublish')}
        </button>
      </div>
      <p className="connection-hint">{t('pubPublishHint')}</p>
    </>}
  </div>;

  const outcome = (r: Publication) => <div className={'publication-outcome' + (r.state === 'Published' ? '' : ' failed')} aria-live="polite">
    {r.state === 'Published'
      ? <><CheckCircle2 size={15} /> {t('pubPublished', { n: r.pullNumber })}{linkOf(r) && <> <a href={linkOf(r)!} target="_blank" rel="noreferrer noopener">{t('pubOpenPull')}</a></>}</>
      : <><AlertTriangle size={15} /> {t('pubFailedAt', { state: t('pubState_' + (r.failedAtState ?? 'Started'), { defaultValue: r.failedAtState ?? '' }), code: r.errorCode ?? '' })}{' '}
        <strong>{r.writesPossible ? t('pubFailedWrites') : t('pubFailedNoWrites')}</strong></>}
  </div>;

  return <div className="publication-plan" aria-label={t('pubTitle')}>
    <h4><GitPullRequest size={15} /> {t('pubTitle')}</h4>
    <p className="connection-hint">{t('pubHint')}</p>
    {error && <div className="alert" role="alert">{error}</div>}
    <div className="publication-form">
      <label>{t('pubDestination')}
        <select value={destinationId} onChange={e => setDestinationId(e.target.value)}>
          {destinations.length === 0 && <option value="">{t('pubNoDestination')}</option>}
          {destinations.map(d => <option key={d.id} value={d.id}>{d.name} · {d.organization}</option>)}
        </select>
      </label>
      <label>{t('pubRepository')}
        <input value={repository} onChange={e => setRepository(e.target.value)} placeholder="repository" />
      </label>
      <label>{t('pubBase')}
        <input value={baseBranch} onChange={e => setBaseBranch(e.target.value)} placeholder={t('pubBasePlaceholder')} />
      </label>
    </div>
    <div className="proposal-actions">
      <button type="button" className="secondary-button" disabled={busy || !destinationId || !repository.trim()} onClick={() => { void run(); }}>
        <GitPullRequest size={14} /> {busy ? t('pubBusy') : t('pubPlan')}
      </button>
    </div>

    {plan && <div className="publication-result">
      <div className="conversion-heading">
        <span className={'connection-badge' + (plan.ready ? '' : ' invalid')}>
          {plan.ready ? <CheckCircle2 size={15} /> : <ShieldAlert size={15} />}
          {plan.ready ? t('pubReady') : blockers.length > 0 ? t('pubBlocked') : t('pubNothing')}
        </span>
        <span className="conversion-reason">{t('pubNoWrites')}</span>
      </div>
      {plan.findings.map(f => <p key={f.code} className="workload-finding"><strong>{f.code}</strong> — {t('pubFinding_' + f.code, { defaultValue: f.message })}</p>)}
      <dl className="conversion-provenance">
        <dt>{t('pubRepo')}</dt><dd>{plan.organization}/{plan.repository}</dd>
        <dt>{t('pubBaseBranch')}</dt><dd>{plan.baseBranch}{plan.baseSha && <> @ <code>{plan.baseSha.slice(0, 12)}</code></>}</dd>
        <dt>{t('pubNewBranch')}</dt><dd><code>{plan.headBranch}</code></dd>
        <dt>{t('pubFile')}</dt><dd><code>{plan.workflowPath}</code></dd>
        <dt>{t('pubPlanHash')}</dt><dd><code className="proposal-hash" title={plan.planSha256}>{plan.planSha256}</code></dd>
      </dl>

      <div className="workload-output-heading"><h5>{t('pubPrTitle')}</h5>{copyButton('title', plan.pullRequestTitle)}</div>
      <p className="publication-title">{plan.pullRequestTitle}</p>
      <div className="workload-output-heading"><h5>{t('pubPrBody')}</h5>{copyButton('body', plan.pullRequestBody)}</div>
      <pre>{plan.pullRequestBody}</pre>
      <div className="workload-output-heading">
        <h5>{t('pubWorkflowFile')}</h5>
        <span className="proposal-actions">
          {copyButton('yaml', plan.workflowYaml)}
          <button className="icon-button" type="button" onClick={() => download(plan)}><Download size={14} /> {t('pubDownload')}</button>
        </span>
      </div>
      <h5>{t('pubSteps')}</h5>
      <ol className="publication-steps">
        {plan.steps.map((s, i) => <li key={i}>
          <span>{t('pubStep_' + s.code, { defaultValue: s.title })}</span>
          {s.command && <span className="publication-command"><code>{s.command}</code>{copyButton('step' + i, s.command)}</span>}
        </li>)}
      </ol>
      {publishSection(plan)}
    </div>}
    {publishError && <div className="alert" role="alert">{publishError}</div>}
    {result && outcome(result)}
    {attempts.length > 0 && <div className="publication-attempts">
      <h5>{t('pubAttempts')}</h5>
      <ul>
        {attempts.map(a => <li key={a.id}>
          <span className={'gate-chip ' + (a.state === 'Published' ? 'gate-passed' : a.state === 'Failed' ? 'gate-failed' : '')}>{t('pubState_' + a.state, { defaultValue: a.state })}</span>{' '}
          {a.organization}/{a.repository} · {new Date(a.startedAtUtc).toLocaleString()} · {a.requestedBy}
          {a.pullNumber ? <> · #{a.pullNumber}</> : null}{a.errorCode ? <> · <code>{a.errorCode}</code></> : null}
          {a.state === 'Failed' && a.writesPossible ? <> · <strong>{t('pubFailedWrites')}</strong></> : null}
        </li>)}
      </ul>
    </div>}
  </div>;
}
