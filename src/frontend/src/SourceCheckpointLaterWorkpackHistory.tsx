import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LockKeyhole, ShieldCheck } from 'lucide-react';
import { api, type CheckpointTargetObservation,
  type LaterCheckpointWorkpackHistory } from './api';

type Props = {
  token: string; preparationId: string; planId: string;
  sourceRepositoryId: string; checkpointId: string;
  sourceSnapshotId: string;
  observation: CheckpointTargetObservation;
};

const guid = (s: unknown): s is string =>
  typeof s === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);
const sha = (s: unknown): s is string =>
  typeof s === 'string' && /^[0-9a-f]{64}$/.test(s);

const fields = new Set([
  'evidenceVersion', 'preparationId', 'planId', 'checkpointId',
  'sourceSnapshotId', 'targetObservationId', 'reconciliationId',
  'workpackId', 'evidenceSha256', 'reconciliationSha256',
  'sourceFullRefProofSha256', 'targetReceiptSha256', 'recordedAtUtc',
  'sourceRefCount', 'targetRefCount', 'matchingCount',
  'createCandidateCount', 'divergedCount', 'targetOnlyCount',
  'state', 'phase', 'historicalOnly', 'currentApprovalVerified',
  'readyForExecution', 'canPush', 'canRetry', 'canDispatch',
  'migrationComplete'
]);

function verify(value: LaterCheckpointWorkpackHistory, props: Props,
  requestedWorkpackId: string): void {
  const numbers = [value.sourceRefCount, value.targetRefCount,
    value.matchingCount, value.createCandidateCount,
    value.divergedCount, value.targetOnlyCount];
  if (Object.keys(value).some(x => !fields.has(x)) ||
      value.evidenceVersion !== 2 ||
      value.preparationId !== props.preparationId ||
      value.planId !== props.planId ||
      value.checkpointId !== props.checkpointId ||
      value.sourceSnapshotId !== props.sourceSnapshotId ||
      value.targetObservationId !== props.observation.id ||
      value.workpackId !== requestedWorkpackId ||
      !guid(value.reconciliationId) || !guid(value.workpackId) ||
      !sha(value.evidenceSha256) ||
      !sha(value.reconciliationSha256) ||
      !sha(value.sourceFullRefProofSha256) ||
      value.targetReceiptSha256 !== props.observation.evidenceSha256 ||
      !Number.isFinite(Date.parse(value.recordedAtUtc)) ||
      !numbers.every(x => Number.isSafeInteger(x) && x >= 0) ||
      value.sourceRefCount < 1 || value.sourceRefCount > 25000 ||
      value.targetRefCount < 1 || value.targetRefCount > 128 ||
      value.matchingCount + value.createCandidateCount +
        value.divergedCount !== value.sourceRefCount ||
      value.matchingCount + value.divergedCount +
        value.targetOnlyCount !== value.targetRefCount ||
      value.state !== 'Blocked' || value.phase !== 'WriteBlocked' ||
      value.historicalOnly !== true ||
      value.currentApprovalVerified !== false ||
      value.readyForExecution !== false ||
      value.canPush !== false || value.canRetry !== false ||
      value.canDispatch !== false || value.migrationComplete !== false)
    throw new Error('LaterCheckpointHistoryUnsafeResponse');
}

// No background polling and no POST: the operator enters an existing
// server-issued workpack UUID and explicitly requests historical proof.
export default function SourceCheckpointLaterWorkpackHistory(props: Props) {
  const { t } = useTranslation();
  const [workpackId, setWorkpackId] = useState('');
  const [loading, setLoading] = useState(false);
  const [missing, setMissing] = useState(false);
  const [error, setError] = useState('');
  const [history, setHistory] = useState<LaterCheckpointWorkpackHistory | null>(null);
  const sequence = useRef(0);
  useEffect(() => () => { ++sequence.current; }, []);

  const inspect = async () => {
    const id = workpackId.trim().toLowerCase();
    if (!guid(id)) return;
    const current = ++sequence.current;
    setLoading(true); setMissing(false); setError(''); setHistory(null);
    try {
      const saved = await api.readLaterCheckpointWorkpackHistory(
        props.token, props.preparationId, props.planId,
        props.checkpointId, id, props.sourceRepositoryId,
        props.observation.id, props.observation.evidenceSha256);
      if (current !== sequence.current) return;
      verify(saved, props, id);
      setHistory(saved);
    } catch (cause) {
      if (current !== sequence.current) return;
      const message = cause instanceof Error ? cause.message : 'UnknownError';
      if (message === 'LaterCheckpointHistoryNotFound' ||
          message === 'LaterCheckpointHistoryDisabled')
        setMissing(true);
      else setError(t('cpLaterHistoryFailed') + ': ' + message);
    } finally {
      if (current === sequence.current) setLoading(false);
    }
  };

  return <section className="checkpoint-human-review"
      aria-label={t('cpLaterHistoryTitle')}>
    <p className="source-cp-warning">
      <LockKeyhole size={15} /> {t('cpLaterHistoryWarning')}
    </p>
    <label>
      {t('cpLaterHistoryUuid')}
      <input type="text" value={workpackId}
        placeholder="xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx"
        onChange={event => {
          ++sequence.current;
          setWorkpackId(event.target.value); setHistory(null);
          setMissing(false); setError(''); setLoading(false);
        }}/>
    </label>
    <button type="button" className="icon-button"
        disabled={!guid(workpackId.trim()) || loading}
        onClick={() => void inspect()}>
      <ShieldCheck size={15}/>
      {loading ? t('cpLaterHistoryLoading') : t('cpLaterHistoryInspect')}
    </button>
    {missing && <p role="status" className="muted">{t('cpLaterHistoryMissing')}</p>}
    {error && <p role="alert" className="alert">{error}</p>}
    {history && <div className="checkpoint-human-review-summary">
      <strong>{t('cpLaterHistoryVerified')}</strong>
      <small>{t('cpLaterHistoryCounts')}: {history.sourceRefCount} / {
        history.targetRefCount} · {history.matchingCount} = · {
        history.createCandidateCount} + · {history.divergedCount} Δ</small>
      <small>{t('cpLaterHistoryState')}: {history.state} / {history.phase}</small>
      <small>{t('cpLaterHistorySavedAt')}: {
        new Date(history.recordedAtUtc).toLocaleString()}</small>
      <code title={history.evidenceSha256}>
        {history.evidenceSha256.slice(0, 20)}…</code>
    </div>}
  </section>;
}
