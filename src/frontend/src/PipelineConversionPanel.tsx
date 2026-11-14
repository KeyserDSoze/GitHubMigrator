import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bot, CheckCircle2, ClipboardCopy, History, ShieldAlert, XCircle, MinusCircle } from 'lucide-react';
import ProposalReview from './ProposalReview';
import { api, type AiConnection, type Connection, type ConversionProposal, type ConversionStatus, type ProposalDetail, type ProposalSummary } from './api';

// A kept proposal, shown with the same pieces as a fresh answer. Every state but Blocked passed the gates when it was proposed.
const fromDetail = (d: ProposalDetail): ConversionProposal => ({
  status: d.state === 'Blocked' ? 'Blocked' : 'ReadyForReview', reason: d.reason, workflowYaml: d.workflowYaml, workflowSha256: d.workflowSha256,
  modelNotes: d.modelNotes, approvable: d.state === 'Proposed', proposalOnly: true, saved: true, proposalId: d.id, proposalState: d.state,
  gates: d.gates, findings: d.findings, plan: d.plan, provenance: d.provenance
});

type Props = {
  token: string; sourceId: string; projectId: string; pipelineId: string;
  destinations: Connection[]; defaultDestinationId: string; defaultRepository: string; isSuperAdmin: boolean;
};

export default function PipelineConversionPanel({ token, sourceId, projectId, pipelineId, destinations, defaultDestinationId, defaultRepository, isSuperAdmin }: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [profiles, setProfiles] = useState<AiConnection[]>([]);
  const [status, setStatus] = useState<ConversionStatus | null>(null);
  const [aiId, setAiId] = useState('');
  const [proposal, setProposal] = useState<ConversionProposal | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState(false);
  const [detail, setDetail] = useState<ProposalDetail | null>(null);
  const [history, setHistory] = useState<ProposalSummary[]>([]);
  const [historyOff, setHistoryOff] = useState(false);

  // The kept proposals are a convenience: when they cannot be loaded the panel still proposes, and says so quietly.
  async function loadHistory() {
    const number = Number(pipelineId);
    if (!sourceId || !projectId || !Number.isSafeInteger(number) || number < 1) { setHistory([]); return; }
    try { setHistory(await api.listProposals(token, sourceId, projectId, number)); setHistoryOff(false); }
    catch { setHistory([]); setHistoryOff(true); }
  }

  async function toggle() {
    const next = !open;
    setOpen(next);
    if (!next) return;
    void loadHistory();
    if (profiles.length || status) return;
    setError('');
    try {
      const [list, analysers] = await Promise.all([api.listAiConnections(token), api.conversionStatus(token)]);
      setProfiles(list); setStatus(analysers);
      const usable = list.find(p => p.verificationCode === 'Verified' && p.sendSourceContent);
      if (usable) setAiId(usable.id);
    } catch { setError(t('convLoadError')); }
  }

  async function openProposal(id: string) {
    setError(''); setCopied(false);
    try { const d = await api.getProposal(token, id); setDetail(d); setProposal(fromDetail(d)); }
    catch { setError(t('convRequestError')); }
  }

  async function propose() {
    const number = Number(pipelineId);
    if (!Number.isSafeInteger(number) || number < 1) { setError(t('convNeedPipeline')); return; }
    setBusy(true); setError(''); setProposal(null); setDetail(null); setCopied(false);
    try {
      const answer = await api.convertPipeline(token, sourceId, projectId, number, aiId);
      setProposal(answer);
      if (answer.proposalId) {
        // the kept copy is what a person approves: read it back instead of trusting the answer in hand
        try { setDetail(await api.getProposal(token, answer.proposalId)); } catch { /* shown without the review step */ }
      }
      void loadHistory();
    }
    catch { setError(t('convRequestError')); }
    finally { setBusy(false); }
  }

  function decided(updated: ProposalDetail) {
    setDetail(updated); setProposal(fromDetail(updated));
    void loadHistory();
  }

  // the derived proposal is a proposal of its own: it replaces the one on screen and is listed with the others
  function derived(created: ProposalDetail) {
    setCopied(false); setError('');
    decided(created);
  }

  async function copy(text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(true); } catch { setError(t('convRequestError')); }
  }

  const reasonText = (reason: string | null) => {
    if (!reason) return '';
    if (reason.startsWith('ModelFailure:')) return t('convReasonModelFailure', { code: reason.slice('ModelFailure:'.length) });
    return t('convReason_' + reason, { defaultValue: reason });
  };
  const blockers = proposal?.findings.filter(f => f.severity === 'Blocker') ?? [];
  const warnings = proposal?.findings.filter(f => f.severity === 'Warning') ?? [];
  const infos = proposal?.findings.filter(f => f.severity === 'Info') ?? [];
  const usable = (p: AiConnection) => p.verificationCode === 'Verified' && p.sendSourceContent;
  const gateIcon = (s: string) => s === 'Passed' ? <CheckCircle2 size={13} /> : s === 'Failed' ? <XCircle size={13} /> : <MinusCircle size={13} />;

  return <section className="conversion-panel" aria-label={t('convTitle')}>
    <button type="button" className="secondary-button" aria-expanded={open} onClick={() => { void toggle(); }}>
      <Bot size={14} /> {t('convTitle')}
    </button>
    {open && <div className="conversion-body">
      <div className="workload-notice"><ShieldAlert size={16} />{t('convDisclaimer')}</div>
      {error && <div className="alert" role="alert">{error}</div>}
      {status && !status.canBeApprovable && <div className="workload-notice">
        <ShieldAlert size={16} />{t('convAnalysersMissing', { missing: [!status.actionlint && 'actionlint', !status.zizmor && 'zizmor'].filter(Boolean).join(', ') })}
      </div>}
      <label>{t('convProfile')}
        <select aria-label={t('convProfile')} value={aiId} onChange={e => setAiId(e.target.value)}>
          {!profiles.length && <option value="">{t('convNoProfiles')}</option>}
          {profiles.map(p => <option key={p.id} value={p.id} disabled={!usable(p)}>
            {p.name} · {p.model}{usable(p) ? '' : ` (${p.verificationCode !== 'Verified' ? t('convProfileUnverified') : t('convProfileNoConsent')})`}
          </option>)}
        </select>
      </label>
      <button type="button" className="primary" disabled={busy || !aiId || !sourceId || !projectId || !pipelineId}
        onClick={() => { void propose(); }}>
        <Bot size={15} /> {busy ? t('convBusy') : t('convPropose')}
      </button>

      <div className="proposal-history">
        <div className="workload-output-heading">
          <h4><History size={14} /> {t('convHistory')}</h4>
          <button className="icon-button" type="button" onClick={() => { void loadHistory(); }}>{t('convHistoryRefresh')}</button>
        </div>
        {historyOff && <p className="connection-hint">{t('convHistoryOff')}</p>}
        {!historyOff && history.length === 0 && <p className="connection-hint">{t('convHistoryEmpty')}</p>}
        {history.length > 0 && <ul>
          {history.map(h => <li key={h.id}>
            <span className={'gate-chip state-' + h.state.toLowerCase()}>{t('convState_' + h.state)}</span>{' '}
            {h.kind === 'Triggers' && <><span className="gate-chip kind-triggers">{t('convKind_Triggers')}</span>{' '}</>}
            {new Date(h.createdAtUtc).toLocaleString()} · {h.createdBy} · <code>{h.workflowSha256?.slice(0, 12) ?? '—'}</code>{' '}
            <button type="button" className="icon-button" onClick={() => { void openProposal(h.id); }}>{t('convHistoryOpen')}</button>
          </li>)}
        </ul>}
      </div>

      {proposal && <div className="conversion-result" role="status">
        <div className="conversion-heading">
          <span className={'connection-badge' + (proposal.status === 'ReadyForReview' && !['Rejected', 'Revoked'].includes(detail?.state ?? '') ? '' : ' invalid')}>
            {proposal.status === 'ReadyForReview' ? <CheckCircle2 size={15} /> : <ShieldAlert size={15} />}
            {detail && detail.state !== 'Blocked' ? t('convState_' + detail.state) : t('convStatus_' + proposal.status)}
          </span>
          {proposal.reason && <span className="conversion-reason">{reasonText(proposal.reason)}</span>}
        </div>
        {detail?.kind === 'Triggers' && <span className="gate-chip kind-triggers">{t('convKind_Triggers')}</span>}
        <p className="connection-hint">{proposal.status !== 'ReadyForReview' ? t('convNotReadyHint')
          : detail?.state === 'Approved' ? t(detail.kind === 'Triggers' ? 'convTriggersApprovedHint' : 'convApprovedHint')
          : t(detail?.kind === 'Triggers' ? 'convTriggersReadyHint' : 'convReadyHint')}</p>
        {proposal.saved === false && proposal.status !== 'NotAttempted' && <p className="workload-notice"><ShieldAlert size={16} />{t('convNotKept')}</p>}

        {proposal.gates.length > 0 && <div className="conversion-gates" aria-label={t('convGates')}>
          {proposal.gates.map(g => <span key={g.name} className={'gate-chip gate-' + g.status.toLowerCase()} title={g.detail ?? ''}>
            {gateIcon(g.status)} {g.name}: {t('convGate_' + g.status)}
          </span>)}
        </div>}

        {blockers.length > 0 && <><h4>{t('convBlockers')} ({blockers.length})</h4>
          {blockers.map((f, i) => <p key={i} className="workload-finding"><strong>{f.code}</strong>{f.line ? ` · ${t('convLine')} ${f.line}` : ''} — {f.message}</p>)}</>}
        {warnings.length > 0 && <details><summary>{t('convWarnings')} ({warnings.length})</summary>
          {warnings.map((f, i) => <p key={i} className="workload-finding"><strong>{f.code}</strong> — {f.message}</p>)}</details>}
        {infos.length > 0 && <p className="connection-hint">{t('convInfos', { count: infos.length })}</p>}

        {proposal.workflowYaml && <>
          <div className="workload-output-heading">
            <h4>{t('convWorkflow')}</h4>
            <button className="icon-button" type="button" onClick={() => { void copy(proposal.workflowYaml ?? ''); }}>
              <ClipboardCopy size={14} /> {copied ? t('workloadCopied') : t('workloadCopy')}
            </button>
          </div>
          <pre>{proposal.workflowYaml}</pre>
        </>}
        {proposal.modelNotes && <><h4>{t('convNotes')}</h4><pre className="conversion-notes">{proposal.modelNotes}</pre></>}

        <h4>{t('convPlan')}</h4>
        <p className="connection-hint">{t('convPlanHint')}</p>
        <p><strong>{t('convSecrets')}:</strong> {proposal.plan.secrets.length ? proposal.plan.secrets.map(s => s.name).join(', ') : '—'}</p>
        <p><strong>{t('convVariables')}:</strong> {proposal.plan.variables.length ? proposal.plan.variables.map(s => s.name).join(', ') : '—'}</p>
        {(proposal.plan.runnerLabels?.length ?? 0) > 0 && <p><strong>{t('convRunnerLabels')}:</strong> {proposal.plan.runnerLabels!.join(', ')} <span className="connection-hint">{t('convRunnerLabelsHint')}</span></p>}

        {proposal.provenance && <><h4>{t('convProvenance')}</h4>
          <dl className="conversion-provenance">
            <dt>{t('convModel')}</dt><dd>{proposal.provenance.model} @ {proposal.provenance.host}</dd>
            <dt>{t('convAttempts')}</dt><dd>{proposal.provenance.attempts.map(a => `${a.number}: ${a.outcome}`).join(' → ')}</dd>
            <dt>{t('convTokens')}</dt><dd>{proposal.provenance.totalPromptTokens} / {proposal.provenance.totalCompletionTokens}</dd>
            <dt>{t('convRedactions')}</dt><dd>{proposal.provenance.redactions.length ? proposal.provenance.redactions.map(r => `${r.kind} × ${r.count}`).join(', ') : t('convNoRedactions')}</dd>
            <dt>{t('convHashes')}</dt><dd><code>{proposal.workflowSha256?.slice(0, 12) ?? '—'}</code> · <code>{proposal.provenance.promptSha256.slice(0, 12)}</code></dd>
            <dt>{t('convPolicy')}</dt><dd>{proposal.provenance.policyVersion} · {t('convCatalog')} {proposal.provenance.actionCatalogVerifiedOn}</dd>
          </dl></>}

        {/* last on purpose: a person reads the text, the findings and the evidence above, then decides */}
        {detail && detail.state !== 'Blocked' && <ProposalReview key={detail.id + detail.state} token={token} detail={detail} onChanged={decided}
          destinations={destinations} defaultDestinationId={defaultDestinationId} defaultRepository={defaultRepository} isSuperAdmin={isSuperAdmin}
          onDerived={derived} onOpenParent={id => { void openProposal(id); }} />}
      </div>}
    </div>}
  </section>;
}
