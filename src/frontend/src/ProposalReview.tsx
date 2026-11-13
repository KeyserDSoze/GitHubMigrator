import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, ShieldCheck, Undo2, UserCheck, Workflow, XCircle } from 'lucide-react';
import PublicationPlanPanel from './PublicationPlanPanel';
import { api, ApiError, type Connection, type ProposalDetail } from './api';

type Props = {
  token: string; detail: ProposalDetail; onChanged: (detail: ProposalDetail) => void;
  destinations: Connection[]; defaultDestinationId: string; defaultRepository: string; isSuperAdmin: boolean;
  // a derived proposal (the Azure triggers) was made from this approved conversion, or a person asked to see the conversion a derived one came from
  onDerived?: (derived: ProposalDetail) => void; onOpenParent?: (id: string) => void;
};

// The human step. Approving binds a person to ONE text, named by its SHA-256, after the server ran the gates again; it publishes
// nothing and re-enables nothing. Warnings are accepted by name, one by one, because they are what a reviewer is there to weigh.
export default function ProposalReview({ token, detail, onChanged, destinations, defaultDestinationId, defaultRepository, isSuperAdmin, onDerived, onOpenParent }: Props) {
  const { t, i18n } = useTranslation();
  const [ack, setAck] = useState<Set<string>>(new Set());
  const [late, setLate] = useState<string[]>([]);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [deriving, setDeriving] = useState(false);
  const [deriveError, setDeriveError] = useState('');

  const seen = new Map(detail.findings.filter(f => f.severity === 'Warning').map(f => [f.code, f.message]));
  const required = [...new Set([...seen.keys(), ...late])];
  const allAccepted = required.every(code => ack.has(code));
  const noteOk = note.trim().length >= 3;
  const when = (iso: string) => new Date(iso).toLocaleString(i18n.language);

  async function run(action: () => Promise<ProposalDetail>) {
    setBusy(true); setError('');
    try { onChanged(await action()); setNote(''); }
    catch (e) {
      const code = e instanceof Error ? e.message : '';
      const details = e instanceof ApiError ? e.details : [];
      // the fresh run may warn about more than the proposal showed: ask for those too, by name
      if (code === 'WarningsNotAcknowledged') setLate(previous => [...new Set([...previous, ...details])]);
      setError(t('reviewError_' + code, { defaultValue: t('reviewErrorGeneric') }) + (details.length ? ` (${details.join(', ')})` : ''));
    } finally { setBusy(false); }
  }

  // Reads the pipeline's own YAML on the server (no model sees it), maps its triggers by fixed rules and keeps the result as a proposal of its own.
  async function deriveTriggers() {
    setDeriving(true); setDeriveError('');
    try { onDerived?.(await api.deriveTriggers(token, detail.id)); }
    catch (e) {
      const code = e instanceof Error ? e.message : '';
      setDeriveError(t('trigError_' + code, { defaultValue: t('reviewError_' + code, { defaultValue: t('trigErrorGeneric') }) }));
    } finally { setDeriving(false); }
  }

  function toggle(code: string) {
    setAck(previous => { const next = new Set(previous); if (next.has(code)) next.delete(code); else next.add(code); return next; });
  }

  const approval = detail.events.find(e => e.type === 'Approved');
  const proposed = detail.state === 'Proposed';
  const triggers = detail.kind === 'Triggers';

  return <div className="proposal-review" aria-label={t('reviewTitle')}>
    <h4><ShieldCheck size={15} /> {t('reviewTitle')}</h4>
    <p className="connection-hint">{t('reviewHash')}: <code className="proposal-hash" title={detail.workflowSha256 ?? ''}>{detail.workflowSha256 ?? '—'}</code></p>
    {!detail.integrityOk && <div className="alert" role="alert">{t('reviewIntegrity')}</div>}
    {triggers && <div className="proposal-triggers" aria-label={t('trigMappedTitle')}>
      <h5><Workflow size={14} /> {t('trigMappedTitle')}</h5>
      <p className="connection-hint">{t('trigMappedHint')}</p>
      <ul>{(detail.triggers?.mapped ?? []).map(line => <li key={line}><code>{line}</code></li>)}</ul>
      {detail.parentProposalId && onOpenParent && <button type="button" className="icon-button" onClick={() => onOpenParent(detail.parentProposalId!)}>{t('trigOpenParent')}</button>}
    </div>}
    {error && <div className="alert" role="alert">{error}</div>}

    {proposed && <>
      {detail.requiresSecondApprover && detail.createdByMe && <p className="workload-notice"><UserCheck size={16} />{t('reviewNeedsSecond')}</p>}
      {required.length > 0 && detail.canApprove && <fieldset className="proposal-warnings">
        <legend>{t('reviewWarningsTitle')}</legend>
        {required.map(code => <label key={code} className="proposal-warning">
          <input type="checkbox" checked={ack.has(code)} onChange={() => toggle(code)} />
          <span><strong>{code}</strong>{seen.get(code) ? ` — ${seen.get(code)}` : ''}</span>
        </label>)}
      </fieldset>}
      <label>{t('reviewNote')}
        <textarea value={note} maxLength={500} rows={2} onChange={e => setNote(e.target.value)} />
      </label>
      <div className="proposal-actions">
        {detail.canApprove && <button type="button" className="primary" disabled={busy || !detail.integrityOk || !allAccepted || !detail.workflowSha256}
          onClick={() => { void run(() => api.approveProposal(token, detail.id, detail.workflowSha256 ?? '', [...ack], note.trim())); }}>
          <CheckCircle2 size={15} /> {t('reviewApprove')}
        </button>}
        <button type="button" className="secondary-button" disabled={busy || !noteOk}
          onClick={() => { void run(() => api.rejectProposal(token, detail.id, note.trim())); }}>
          <XCircle size={14} /> {t('reviewReject')}
        </button>
      </div>
      {detail.canApprove && <p className="connection-hint">{t(triggers ? 'reviewApproveHintTriggers' : 'reviewApproveHint')}</p>}
    </>}

    {detail.state === 'Approved' && <>
      <PublicationPlanPanel token={token} proposalId={detail.id} destinations={destinations} defaultDestinationId={defaultDestinationId} defaultRepository={defaultRepository} isSuperAdmin={isSuperAdmin} />
      <p className="connection-hint">{t('reviewApprovedHint')}</p>
      {!triggers && onDerived && <div className="proposal-triggers" aria-label={t('trigDeriveTitle')}>
        <h5><Workflow size={14} /> {t('trigDeriveTitle')}</h5>
        <p className="connection-hint">{t('trigDeriveHint')}</p>
        {deriveError && <div className="alert" role="alert">{deriveError}</div>}
        <button type="button" className="secondary-button" disabled={deriving} onClick={() => { void deriveTriggers(); }}>
          <Workflow size={14} /> {deriving ? t('trigBusy') : t('trigDerive')}
        </button>
      </div>}
      <label>{t('reviewNote')}
        <textarea value={note} maxLength={500} rows={2} onChange={e => setNote(e.target.value)} />
      </label>
      <div className="proposal-actions">
        <button type="button" className="secondary-button" disabled={busy || !noteOk}
          onClick={() => { void run(() => api.revokeProposal(token, detail.id, note.trim())); }}>
          <Undo2 size={14} /> {t('reviewRevoke')}
        </button>
      </div>
    </>}

    {approval && <p className="connection-hint">{t('reviewApprovedBy', { actor: approval.actor, when: when(approval.occurredAtUtc) })}</p>}
    <h5>{t('reviewHistory')}</h5>
    <ol className="proposal-events">
      {detail.events.map(e => <li key={e.id}>
        <strong>{t('reviewEvent_' + e.type)}</strong> · {e.actor} · {when(e.occurredAtUtc)}
        {e.note && <span className="proposal-note"> — {e.note}</span>}
        {e.type === 'Approved' && e.acknowledgedWarnings.length > 0 && <span className="connection-hint"> ({t('reviewAccepted')}: {e.acknowledgedWarnings.join(', ')})</span>}
      </li>)}
    </ol>
  </div>;
}
