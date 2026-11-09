import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LockKeyhole, ShieldCheck } from 'lucide-react';
import {
  api, type CheckpointHumanReviewReceipt,
  type CheckpointTargetObservation, type SourceSyncCheckpointReceipt
} from './api';

type Props = {
  token: string; preparationId: string; planId: string;
  sourceRepositoryId: string; targetRepositoryName: string;
  checkpoint: SourceSyncCheckpointReceipt;
  human: CheckpointHumanReviewReceipt;
};

const sha = (s: unknown): s is string =>
  typeof s === 'string' && /^[0-9a-f]{64}$/.test(s);
const guid = (s: unknown): s is string =>
  typeof s === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);

function verify(record: CheckpointTargetObservation, props: Props) {
  if (record.evidenceVersion !== 1 ||
      !guid(record.id) || !guid(record.reviewedBy) ||
      record.preparationId !== props.preparationId ||
      record.planId !== props.planId ||
      record.checkpointId !== props.checkpoint.id ||
      record.sourceSnapshotId !== props.checkpoint.proof.snapshotId ||
      record.sourceRepositoryId !== props.sourceRepositoryId ||
      record.targetRepositoryName !== props.targetRepositoryName ||
      record.candidateEvidenceSha256 !==
        props.human.candidateEvidenceSha256 ||
      record.humanReviewEvidenceSha256 !== props.human.evidenceSha256 ||
      record.reviewedBy !== props.human.reviewedBy ||
      record.objectFormat !== 'sha1' && record.objectFormat !== 'sha256' ||
      !sha(record.evidenceSha256) || !sha(record.targetEvidenceSha256) ||
      !Number.isSafeInteger(record.refCount) ||
      record.refCount < 1 || record.refCount > 128 ||
      !Number.isFinite(Date.parse(record.observedAtUtc)) ||
      Date.parse(record.observedAtUtc) <
        Date.parse(props.human.reviewedAtUtc) ||
      record.canObserveTarget !== false ||
      record.canPush !== false || record.canRetry !== false ||
      record.canDispatch !== false || record.migrationComplete !== false)
    throw new Error('CheckpointTargetObservationUnsafeResponse');
}

// Explicit-user-action GET only. It reads the already committed F-031
// checkpoint observation; it NEVER causes a provider GET/POST or a push.
export default function SourceCheckpointTargetObservationHistory(props: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [receipt, setReceipt] = useState<CheckpointTargetObservation | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [error, setError] = useState('');
  const sequence = useRef(0);
  useEffect(() => () => { ++sequence.current; }, []);

  const inspect = async () => {
    const request = ++sequence.current;
    setLoading(true); setReceipt(null); setError(''); setNotFound(false);
    try {
      const result = await api.readCheckpointTargetObservation(
        props.token, props.preparationId, props.planId,
        props.checkpoint.id, props.sourceRepositoryId);
      if (request !== sequence.current) return;
      verify(result, props);
      setReceipt(result);
    } catch (cause) {
      if (request !== sequence.current) return;
      const message = cause instanceof Error ? cause.message : 'UnknownError';
      if (message === 'CheckpointTargetObservationNotFound' ||
          message === 'CheckpointTargetObservationDisabled') setNotFound(true);
      else setError(t('cpTargetAuditFailed') + ': ' + message);
    } finally {
      if (request === sequence.current) setLoading(false);
    }
  };

  return <section className="checkpoint-human-review"
      aria-label={t('cpTargetAudit') + ' · ' + props.targetRepositoryName}>
    <button type="button" className="icon-button"
        aria-expanded={open} onClick={() => {
          if (open) {
            ++sequence.current; setOpen(false); setLoading(false);
          } else {
            setOpen(true); void inspect();
          }
        }}>
      <ShieldCheck size={15} />
      {open ? t('cpTargetAuditHide') : t('cpTargetAudit')}
    </button>
    {open && <>
      <p className="source-cp-warning">
        <LockKeyhole size={15} />{t('cpTargetAuditWarning')}
      </p>
      {loading && <p role="status" className="muted">{t('cpTargetAuditLoading')}</p>}
      {notFound && <p role="status" className="muted">{t('cpTargetAuditMissing')}</p>}
      {error && <p role="alert" className="alert">{error}</p>}
      {receipt && <div className="checkpoint-human-review-summary">
        <strong>{t('cpTargetAuditSaved')}</strong>
        <small>{props.targetRepositoryName} · {receipt.objectFormat} · {
          receipt.refCount} {t('cpRefs')}</small>
        <small>{t('cpTargetAuditObserved')}: {
          new Date(receipt.observedAtUtc).toLocaleString()}</small>
        <small>{t('cpHumanEvidence')}</small>
        <code title={receipt.evidenceSha256}>
          {receipt.evidenceSha256.slice(0, 20)}…</code>
      </div>}
    </>}
  </section>;
}
