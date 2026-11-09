import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  api, type CheckpointTargetRefReviewPage,
  type CheckpointTargetReviewItem, type GitHubTargetRefEvidence,
  type SourceSyncCheckpointReceipt
} from './api';

type Props = {
  receipt: SourceSyncCheckpointReceipt;
  token: string; preparationId: string; planId: string;
};

const sha = (value: unknown): value is string =>
  typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const decisions: Record<string, [string, string]> = {
  Added: ['SourceAddedTargetAbsentReview', 'SourceAddedTargetPresentReview'],
  Restored: ['SourceRestoredTargetAbsentReview', 'SourceRestoredTargetPresentReview'],
  Changed: ['SourceChangedTargetAbsentBlocked', 'SourceChangedTargetPresentBlocked'],
  Missing: ['SourceMissingTargetAbsentPreserved', 'SourceMissingTargetPresentPreserved']
};

function validateReview(page: CheckpointTargetRefReviewPage,
  receipt: SourceSyncCheckpointReceipt, target: GitHubTargetRefEvidence,
  after: string | null) {
  const repo = receipt.proof.repositories.find(x =>
    x.sourceRepositoryId === target.sourceRepositoryId &&
    x.targetRepositoryName === target.targetRepositoryName);
  if (!repo || receipt.evidenceVersion !== 2 ||
      page.checkpointId !== receipt.id ||
      page.sourceSnapshotId !== receipt.proof.snapshotId ||
      page.sourceRepositoryId !== target.sourceRepositoryId ||
      page.targetRepositoryName !== target.targetRepositoryName ||
      page.targetObservationId !== target.id ||
      page.targetEvidenceSha256 !== target.evidenceSha256 ||
      page.sourceRefManifestSha256 !== receipt.refChangeManifest?.evidenceSha256 ||
      page.sourceRefAttestationSha256 !== receipt.refChangeAttestationSha256 ||
      !sha(page.targetEvidenceSha256) ||
      !sha(page.sourceRefManifestSha256) ||
      !sha(page.sourceRefAttestationSha256) ||
      page.targetObservedAtUtc !== target.observedAtUtc ||
      page.repositoryRefChanges !== repo.total ||
      page.canPush || page.canRetry || page.canDispatch ||
      page.migrationComplete ||
      !Array.isArray(page.items) || page.items.length > 50 ||
      (page.nextAfter !== null &&
        (page.items.length === 0 ||
         page.nextAfter !== page.items[page.items.length - 1].sourceChange.sourceId)))
    throw new Error('SourceSyncCheckpointUnsafeResponse');

  let last = after;
  for (const item of page.items) {
    const change = item?.sourceChange;
    const allowed = change && decisions[change.kind];
    if (!change || !allowed ||
        typeof change.sourceId !== 'string' ||
        typeof change.refName !== 'string' ||
        !change.refName.startsWith('refs/') ||
        change.sourceRepositoryId !== target.sourceRepositoryId ||
        change.targetRepositoryName !== target.targetRepositoryName ||
        change.sourceId !== JSON.stringify([change.sourceRepositoryId,
          change.refName]) ||
        (last !== null && change.sourceId <= last) ||
        !Number.isSafeInteger(change.revision) || change.revision < 1 ||
        (change.previousFingerprint !== null && !sha(change.previousFingerprint)) ||
        (change.currentFingerprint !== null && !sha(change.currentFingerprint)) ||
        (change.kind === 'Added' &&
          (change.previousFingerprint !== null || !sha(change.currentFingerprint))) ||
        (change.kind === 'Changed' &&
          (!sha(change.previousFingerprint) || !sha(change.currentFingerprint) ||
           change.previousFingerprint === change.currentFingerprint)) ||
        (change.kind === 'Restored' &&
          (!sha(change.previousFingerprint) || !sha(change.currentFingerprint))) ||
        (change.kind === 'Missing' &&
          (!sha(change.previousFingerprint) || change.currentFingerprint !== null)) ||
        item.reviewState !== allowed[item.targetRefObserved ? 1 : 0])
      throw new Error('SourceSyncCheckpointUnsafeResponse');
    last = change.sourceId;
  }
}

export default function SourceCheckpointTargetReview({
  receipt, token, preparationId, planId
}: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [options, setOptions] = useState<GitHubTargetRefEvidence[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [loadedOptions, setLoadedOptions] = useState(false);
  const [loading, setLoading] = useState(false);
  const [items, setItems] = useState<CheckpointTargetReviewItem[]>([]);
  const [nextAfter, setNextAfter] = useState<string | null>(null);
  const [loadedReview, setLoadedReview] = useState(false);
  const [error, setError] = useState('');
  const sequence = useRef(0);
  useEffect(() => () => { ++sequence.current; }, []);

  const available = async () => {
    const request = ++sequence.current;
    setLoading(true); setError('');
    try {
      const candidates = await api.listGitHubTargetRefEvidence(
        token, preparationId, planId);
      if (request !== sequence.current) return;
      if (!Array.isArray(candidates) || candidates.some(x => x.readyForExecution))
        throw new Error('SourceSyncCheckpointUnsafeResponse');
      const eligible = candidates.filter(x =>
        x.isCurrent && x.planId === planId &&
        x.manifestSha256 === receipt.proof.manifestSha256 &&
        sha(x.evidenceSha256) &&
        receipt.proof.repositories.some(r =>
          r.sourceRepositoryId === x.sourceRepositoryId &&
          r.targetRepositoryName === x.targetRepositoryName) &&
        Date.parse(x.observedAtUtc) >=
          Date.parse(receipt.proof.stagingAppliedAtUtc));
      setOptions(eligible);
      setSelectedId(eligible[0]?.id ?? '');
      setLoadedOptions(true);
    } catch (cause) {
      if (request === sequence.current)
        setError(t('cpTargetFailed') + ': ' +
          (cause instanceof Error ? cause.message : 'UnknownError'));
    } finally {
      if (request === sequence.current) setLoading(false);
    }
  };

  const review = async (after: string | null = null) => {
    const target = options.find(x => x.id === selectedId);
    if (!target) return;
    const request = ++sequence.current;
    setLoading(true); setError('');
    try {
      const result = await api.reviewCheckpointTargetRefNames(
        token, preparationId, planId, receipt.id, target.id,
        target.sourceRepositoryId, target.evidenceSha256, after, 50);
      if (request !== sequence.current) return;
      validateReview(result, receipt, target, after);
      setItems(old => after === null ? result.items : [...old, ...result.items]);
      setNextAfter(result.nextAfter);
      setLoadedReview(true);
    } catch (cause) {
      if (request === sequence.current)
        setError(t('cpTargetFailed') + ': ' +
          (cause instanceof Error ? cause.message : 'UnknownError'));
    } finally {
      if (request === sequence.current) setLoading(false);
    }
  };

  const toggle = () => {
    if (open) {
      setOpen(false);
      ++sequence.current; setLoading(false);
    } else {
      setOpen(true);
      if (!loadedOptions) void available();
    }
  };

  return <section aria-label={t('cpTargetReview')}>
    <button type="button" className="icon-button"
      aria-expanded={open} onClick={toggle}>
      {open ? t('cpTargetHide') : t('cpTargetReview')}
    </button>
    {open && <>
      <p className="source-cp-warning">{t('cpTargetWarning')}</p>
      {loading && <p className="muted" role="status">{t('cpTargetLoading')}</p>}
      {error && <p className="alert" role="alert">{error}</p>}
      {loadedOptions && options.length === 0 &&
        <p className="muted" role="status">{t('cpTargetMissing')}</p>}
      {options.length > 0 && <>
        <label>{t('cpTargetChoose')}
          <select aria-label={t('cpTargetChoose')}
            value={selectedId} disabled={loading}
            onChange={e => {
              ++sequence.current; setSelectedId(e.target.value);
              setItems([]); setNextAfter(null); setLoadedReview(false);
              setError('');
            }}>
            {options.map(option => <option key={option.id} value={option.id}>
              {option.targetRepositoryName} · {option.sourceRepositoryId} ·
              {new Date(option.observedAtUtc).toLocaleString()}
            </option>)}
          </select>
        </label>
        <button type="button" className="icon-button"
          disabled={loading || !selectedId}
          onClick={() => void review()}>
          {t('cpTargetCompare')}
        </button>
      </>}
      {loadedReview && <div className="source-cp-repositories">
        {items.map(item => <div key={item.sourceChange.sourceId}>
          <span><strong>{item.sourceChange.refName}</strong>
            <small>{item.sourceChange.kind}</small></span>
          <small>{item.targetRefObserved
            ? t('cpTargetObserved') : t('cpTargetAbsent')}</small>
          <small>{item.sourceChange.kind === 'Changed'
            ? t('cpTargetChanged')
            : item.sourceChange.kind === 'Missing'
              ? t('cpTargetMissingRef')
              : item.sourceChange.kind === 'Restored'
                ? t('cpTargetRestored')
                : t('cpTargetAdded')}</small>
        </div>)}
      </div>}
      {nextAfter && <button type="button" className="icon-button"
        disabled={loading} onClick={() => void review(nextAfter)}>
        {t('cpTargetMore')}
      </button>}
    </>}
  </section>;
}
