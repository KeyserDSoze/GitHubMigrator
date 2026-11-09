import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LockKeyhole, ShieldCheck } from 'lucide-react';
import {
  api, type CheckpointHumanReviewCandidate,
  type CheckpointHumanReviewReceipt, type SourceSyncCheckpointReceipt
} from './api';

type Props = {
  receipt: SourceSyncCheckpointReceipt;
  sourceRepositoryId: string; targetRepositoryName: string;
  token: string; preparationId: string; planId: string;
};
type View =
  | { kind: 'candidate'; value: CheckpointHumanReviewCandidate }
  | { kind: 'decision'; value: CheckpointHumanReviewReceipt };
type Authority = {
  canObserveTarget: boolean; canPush: boolean; canRetry: boolean;
  canDispatch: boolean; migrationComplete: boolean;
};
const sha = (v: unknown): v is string =>
  typeof v === 'string' && /^[0-9a-f]{64}$/.test(v);
const guid = (v: unknown): v is string =>
  typeof v === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
const blocked = (v: Authority) =>
  v.canObserveTarget === false && v.canPush === false &&
  v.canRetry === false && v.canDispatch === false &&
  v.migrationComplete === false;

function verify(
  result: View, props: Props
): void {
  const value = result.value;
  if (value.preparationId !== props.preparationId ||
      value.planId !== props.planId ||
      value.checkpointId !== props.receipt.id ||
      value.sourceSnapshotId !== props.receipt.proof.snapshotId ||
      value.sourceRepositoryId !== props.sourceRepositoryId ||
      value.targetRepositoryName !== props.targetRepositoryName ||
      !blocked(value) || !sha(value.evidenceSha256))
    throw new Error('CheckpointHumanReviewUnsafeResponse');
  if (result.kind === 'decision') {
    const record = result.value;
    if (record.evidenceVersion !== 1 ||
        !sha(record.candidateEvidenceSha256) ||
        !guid(record.id) || !guid(record.requestedBy) ||
        !guid(record.reviewedBy) ||
        record.requestedBy === record.reviewedBy ||
        !Number.isFinite(Date.parse(record.reviewedAtUtc)) ||
        !['ApprovedForObservation', 'Rejected'].includes(record.decision))
      throw new Error('CheckpointHumanReviewUnsafeResponse');
  }
}

// No approval/denial action and no destination transport. This is a
// purely GET-only inspection of immutable PostgreSQL evidence.
export default function SourceCheckpointHumanReview(props: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [view, setView] = useState<View | null>(null);
  const [error, setError] = useState('');
  const [missing, setMissing] = useState(false);
  const sequence = useRef(0);
  useEffect(() => () => { ++sequence.current; }, []);

  const inspect = async () => {
    const current = ++sequence.current;
    setLoading(true); setView(null); setError(''); setMissing(false);
    try {
      let result: View;
      try {
        result = { kind: 'decision', value: await api.readCheckpointHumanReview(
          props.token, props.preparationId, props.planId,
          props.receipt.id, props.sourceRepositoryId) };
      } catch (cause) {
        if (!(cause instanceof Error) ||
            cause.message !== 'CheckpointHumanReviewNotFound') throw cause;
        result = { kind: 'candidate', value: await api.previewCheckpointHumanReview(
          props.token, props.preparationId, props.planId,
          props.receipt.id, props.sourceRepositoryId) };
      }
      if (current !== sequence.current) return;
      verify(result, props);
      setView(result);
    } catch (cause) {
      if (current !== sequence.current) return;
      const message = cause instanceof Error ? cause.message : 'UnknownError';
      if (message === 'CheckpointHumanReviewCandidateUnverified' ||
          message === 'CheckpointHumanReviewDisabled' ||
          message === 'CheckpointHumanReviewNotFound') setMissing(true);
      else setError(t('cpHumanFailed') + ': ' + message);
    } finally {
      if (current === sequence.current) setLoading(false);
    }
  };

  const toggle = () => {
    if (open) {
      ++sequence.current; setOpen(false); setLoading(false);
    } else {
      setOpen(true); void inspect();
    }
  };

  return <section className="checkpoint-human-review"
    aria-label={t('cpHumanReview') + ' · ' + props.targetRepositoryName}>
    <button className="icon-button" type="button"
      aria-expanded={open} onClick={toggle}>
      <ShieldCheck size={15} />
      {open ? t('cpHumanHide') : t('cpHumanReview')}
      {' · '}{props.targetRepositoryName}
    </button>
    {open && <>
      <p className="source-cp-warning"><LockKeyhole size={15} />{t('cpHumanIntro')}</p>
      {loading && <p role="status" className="muted">{t('cpHumanLoading')}</p>}
      {error && <p role="alert" className="alert">{error}</p>}
      {missing && <p role="status" className="muted">{t('cpHumanMissing')}</p>}
      {view && <div className="checkpoint-human-review-summary">
        <strong>{view.kind === 'candidate' ? t('cpHumanPreview') :
          view.value.decision === 'Rejected' ? t('cpHumanRejected') :
            t('cpHumanApproved')}</strong>
        <small>{props.sourceRepositoryId} · {props.targetRepositoryName}</small>
        <small>{t('cpHumanEvidence')}</small>
        <code title={view.value.evidenceSha256}>
          {view.value.evidenceSha256.slice(0, 20)}…</code>
        {view.kind === 'decision' &&
          <small>{t('cpHumanReviewedAt')}: {
            new Date(view.value.reviewedAtUtc).toLocaleString()}</small>}
      </div>}
    </>}
  </section>;
}
