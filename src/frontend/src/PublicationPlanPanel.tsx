import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, CheckCircle2, ClipboardCopy, Download, GitPullRequest, RefreshCw, Replace, ShieldAlert } from 'lucide-react';
import { api, ApiError, type Connection, type Publication, type PublicationPlan, type PublicationStatus, type Reconciliation } from './api';

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
  const [reconciled, setReconciled] = useState<Record<string, Reconciliation>>({});
  const [reconciling, setReconciling] = useState('');
  const [reconcileErrors, setReconcileErrors] = useState<Record<string, string>>({});
  const [releaseNotes, setReleaseNotes] = useState<Record<string, string>>({});
  const [releasing, setReleasing] = useState('');
  const [releaseErrors, setReleaseErrors] = useState<Record<string, string>>({});

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

  // Reads the destination (GET only) for one recorded attempt and sets what is there beside what the ledger says. It changes nothing anywhere. The connection used is
  // the one for the organization the attempt wrote to, since reading another would only say "not found".
  async function reconcile(a: Publication) {
    const connection = destinations.find(d => d.organization.toLowerCase() === a.organization.toLowerCase())?.id ?? destinationId;
    setReconciling(a.id); setReconcileErrors(previous => ({ ...previous, [a.id]: '' }));
    try { const seen = await api.reconcilePublication(token, proposalId, a.id, connection); setReconciled(previous => ({ ...previous, [a.id]: seen })); }
    catch (e) {
      const code = e instanceof Error ? e.message : '';
      setReconcileErrors(previous => ({ ...previous, [a.id]: t('pubRecError_' + code, { defaultValue: t('pubRecErrorGeneric') }) }));
    } finally { setReconciling(''); }
  }

  // Frees the exact plan an attempt kept blocked. The product reads the destination again first (GET only) and refuses if the branch, an open pull request or a merged one is still
  // there; it writes nothing at the destination and the attempt stays on record as it was. Only a superadmin gets here.
  async function release(a: Publication) {
    const connection = destinations.find(d => d.organization.toLowerCase() === a.organization.toLowerCase())?.id ?? destinationId;
    setReleasing(a.id); setReleaseErrors(previous => ({ ...previous, [a.id]: '' }));
    try {
      await api.releasePublication(token, proposalId, a.id, connection, (releaseNotes[a.id] ?? '').trim());
      setReleaseNotes(previous => ({ ...previous, [a.id]: '' }));
      // what was planned before the release said the attempt was in the way: it is read again, not edited
      setPlan(null); setConfirmed(false);
      await reconcile(a);
    } catch (e) {
      const code = e instanceof Error ? e.message : '';
      const named = (e instanceof ApiError ? e.details : []).map(d => t('pubLeftover_' + d, { defaultValue: d })).join('; ');
      setReleaseErrors(previous => ({ ...previous, [a.id]: t('pubReleaseError_' + code, { defaultValue: t('pubReleaseErrorGeneric') }) + (named ? ` (${named})` : '') }));
      if (code === 'PublicationLeftoversExist' || code === 'PublicationLedgerConflict') void reconcile(a); // show what is there now
    } finally {
      setReleasing('');
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
        <span>{t(p.mode === 'Update' ? 'pubConfirmUpdate' : 'pubConfirm', { hash: p.planSha256.slice(0, 12) })}</span>
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

  // What the destination shows for one attempt. The verdict is a fact about the destination; the hint says what a person could do, never what the product will do.
  const reconciliation = (r: Reconciliation) => <div className="publication-reconciliation" aria-label={t('pubRecTitle')}>
    <div className="conversion-heading">
      <span className={'connection-badge' + (['Merged', 'OpenPullRequest', 'NothingFound', 'AlreadyOnBase', 'ClosedWithoutMerge'].includes(r.verdict) ? '' : ' invalid')}>
        {['Merged', 'OpenPullRequest'].includes(r.verdict) ? <CheckCircle2 size={15} /> : <ShieldAlert size={15} />} {t('pubVerdict_' + r.verdict, { defaultValue: r.verdict })}
      </span>
      <span className="conversion-reason">{t('pubRecNote')} · {new Date(r.readAtUtc).toLocaleString()}</span>
    </div>
    <p className="connection-hint">{t('pubVerdictHint_' + r.verdict, { defaultValue: '' })}</p>
    {r.findings.map(f => <p key={f.code} className={'workload-finding' + (f.severity === 'Warning' ? ' warning' : '')}>
      <strong>{f.code}</strong> — {t('pubRecFinding_' + f.code, { defaultValue: f.message })}</p>)}
    {r.observed && r.observed.repositoryExists && <dl className="conversion-provenance">
      <dt>{t('pubRecBranch')}</dt><dd>{r.observed.headBranchSha ? <code>{r.observed.headBranchSha.slice(0, 12)}</code> : t('pubRecMissing')}</dd>
      <dt>{t('pubRecBase')}</dt><dd>{r.observed.baseTipSha ? <code>{r.observed.baseTipSha.slice(0, 12)}</code> : t('pubRecMissing')}</dd>
      <dt>{t('pubRecPulls')}</dt><dd>{r.observed.pulls.length === 0 ? t('pubRecNone')
        : r.observed.pulls.map(p => `#${p.number} ${p.merged ? t('pubRecMerged') : p.state === 'open' ? t('pubRecOpen') : t('pubRecClosed')}`).join(', ')}</dd>
      <dt>{t('pubRecFileOnBranch')}</dt><dd>{r.observed.headBranchSha ? t('pubFile_' + r.observed.fileOnHead) : '—'}</dd>
      <dt>{t('pubRecFileOnBase')}</dt><dd>{r.observed.baseTipSha ? t('pubFile_' + r.observed.fileOnBase) : '—'}</dd>
    </dl>}
  </div>;

  // Offered only where the product itself said the ledger still blocks the plan (a read of the destination shows nothing of the attempt left): one rule, the server's.
  const releasable = (r: Reconciliation) => isSuperAdmin && !r.ledgerReleased && r.findings.some(f => f.code === 'LedgerStillBlocksPlan');
  const releaseForm = (a: Publication) => <div className="publication-reconciliation publication-release" aria-label={t('pubReleaseTitle')}>
    <strong>{t('pubReleaseTitle')}</strong>
    <p className="connection-hint">{t('pubReleaseHint')}</p>
    <label>{t('pubReleaseNote')}
      <textarea rows={2} maxLength={500} value={releaseNotes[a.id] ?? ''} onChange={e => setReleaseNotes(previous => ({ ...previous, [a.id]: e.target.value }))} />
    </label>
    <div className="proposal-actions">
      <button type="button" className="secondary-button" disabled={releasing === a.id || (releaseNotes[a.id] ?? '').trim().length < 3} onClick={() => { void release(a); }}>
        <CheckCircle2 size={14} /> {releasing === a.id ? t('pubReleaseBusy') : t('pubReleaseButton')}
      </button>
    </div>
    {releaseErrors[a.id] && <div className="alert" role="alert">{releaseErrors[a.id]}</div>}
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
      {plan.mode === 'Update' && <p className="publication-mode"><span className="gate-chip kind-triggers"><Replace size={13} /> {t('pubModeUpdate')}</span>{' '}
        {t('pubModeUpdateHint', { hash: (plan.replacesWorkflowSha256 ?? '').slice(0, 12) })}</p>}
      {plan.findings.map(f => <p key={f.code} className="workload-finding"><strong>{f.code}</strong> — {t('pubFinding_' + f.code, { defaultValue: f.message, hash: (plan.replacesWorkflowSha256 ?? '').slice(0, 12), number: /#(\d+)/.exec(f.message)?.[1] ?? '?' })}</p>)}
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
          {a.mode === 'Update' && <><span className="gate-chip kind-triggers"><Replace size={13} /> {t('pubAttemptUpdate')}</span>{' '}</>}
          {a.organization}/{a.repository} · {new Date(a.startedAtUtc).toLocaleString()} · {a.requestedBy}
          {a.pullNumber ? <> · #{a.pullNumber}</> : null}{a.errorCode ? <> · <code>{a.errorCode}</code></> : null}
          {a.state === 'Failed' && a.writesPossible ? <> · <strong>{t('pubFailedWrites')}</strong></> : null}{' '}
          <button type="button" className="icon-button" disabled={reconciling === a.id} onClick={() => { void reconcile(a); }}>
            <RefreshCw size={13} /> {reconciling === a.id ? t('pubRecBusy') : t('pubRecCheck')}
          </button>
          {reconcileErrors[a.id] && <div className="alert" role="alert">{reconcileErrors[a.id]}</div>}
          {a.releasedAtUtc && <p className="connection-hint publication-released">
            <CheckCircle2 size={13} /> {t('pubReleasedBy', { who: a.releasedBy ?? '', when: new Date(a.releasedAtUtc).toLocaleString() })}
            {a.releaseNote ? <> — “{a.releaseNote}”</> : null}</p>}
          {reconciled[a.id] && reconciliation(reconciled[a.id])}
          {reconciled[a.id] && releasable(reconciled[a.id]) && releaseForm(a)}
        </li>)}
      </ul>
    </div>}
  </div>;
}
