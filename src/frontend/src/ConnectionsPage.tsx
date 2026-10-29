import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, Cloud, GitBranch, KeyRound, RefreshCw, ShieldAlert, Trash2 } from 'lucide-react';
import { api, type Connection, type ConnectionDraft, type ValidationResult } from './api';

function validationExplanation(code: string, t: (key: string) => string): string {
  if (code === 'Unauthorized') return t('connUnauthorized');
  if (code === 'InsufficientScope') return t('connInsufficientScope');
  if (code === 'OrganizationNotFound') return t('connOrgNotFound');
  if (code === 'InvalidConnectionInput') return t('connInvalidInput');
  return t('connUnavailable');
}

export default function ConnectionsPage({ token, isAdmin }: { token: string; isAdmin: boolean }) {
  const { t } = useTranslation();
  const [connections, setConnections] = useState<Connection[]>([]);
  const [draft, setDraft] = useState<ConnectionDraft>({ name: '', providerKey: 'azure-devops-services', organization: '', personalAccessToken: '' });
  const [validation, setValidation] = useState<ValidationResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [rotationId, setRotationId] = useState<string | null>(null);
  const [replacementPat, setReplacementPat] = useState('');
  const [feedback, setFeedback] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    if (!isAdmin) { setLoading(false); return; }
    let current = true;
    api.listConnections(token)
      .then(items => { if (current) setConnections(items); })
      .catch(() => { if (current) setError(t('operationError')); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, isAdmin, t]);

  const change = (field: keyof ConnectionDraft, value: string) => {
    setDraft(current => ({ ...current, [field]: value }));
    setValidation(null); setError(''); setFeedback('');
  };

  async function testConnection() {
    setBusy(true); setError(''); setFeedback(''); setValidation(null);
    try {
      const result = await api.validateConnection(token, draft);
      setValidation(result);
      if (!result.isValid) setError(validationExplanation(result.code, t));
    } catch (reason) {
      setError(validationExplanation(reason instanceof Error ? reason.message : '', t));
    } finally { setBusy(false); }
  }

  async function saveConnection(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(''); setFeedback('');
    try {
      const item = await api.saveConnection(token, draft);
      setConnections(current => [...current, item]);
      setDraft({ name: '', providerKey: 'azure-devops-services', organization: '', personalAccessToken: '' });
      setValidation(null); setFeedback(t('connSuccess'));
    } catch (reason) {
      setError(validationExplanation(reason instanceof Error ? reason.message : '', t));
    } finally { setBusy(false); }
  }

  async function verify(item: Connection) {
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      const next = await api.verifyConnection(token, item.id);
      setConnections(current => current.map(c => c.id === next.id ? next : c));
      if (next.validationCode !== 'Verified') setError(validationExplanation(next.validationCode, t));
    } catch { setError(t('operationError')); }
    finally { setBusyId(null); }
  }

  async function rotate(item: Connection) {
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      const next = await api.rotateCredential(token, item.id, replacementPat);
      setConnections(current => current.map(c => c.id === next.id ? next : c));
      setRotationId(null); setReplacementPat('');
      setFeedback(t('connRotated'));
    } catch (reason) { setError(validationExplanation(reason instanceof Error ? reason.message : '', t)); }
    finally { setBusyId(null); }
  }

  async function remove(item: Connection) {
    if (!window.confirm(t('connRemoveConfirm'))) return;
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      await api.deleteConnection(token, item.id);
      setConnections(current => current.filter(c => c.id !== item.id));
    } catch { setError(t('operationError')); }
    finally { setBusyId(null); }
  }

  if (!isAdmin) return <section className="empty-card"><ShieldAlert size={30} /><p>{t('accessDenied')}</p></section>;

  return <div className="connection-layout">
    <section className="connection-form" aria-label={t('connNew')}>
      <h2>{t('connNew')}</h2>
      <p>{t('connIntro')}</p>
      <form onSubmit={saveConnection}>
        <label>{t('connProvider')}
          <select aria-label={t('connProvider')} value={draft.providerKey} onChange={event => change('providerKey', event.target.value)}>
            <option value="azure-devops-services">Azure DevOps Services · {t('connSource')}</option>
            <option value="github-enterprise-cloud">GitHub Enterprise Cloud · {t('connDestination')}</option>
          </select>
        </label>
        <label>{t('connName')}<input required maxLength={120} value={draft.name}
          onChange={event => change('name', event.target.value)} placeholder="My migration source" /></label>
        <label>{t('connOrganization')}<input required pattern="[A-Za-z0-9][A-Za-z0-9_-]*" maxLength={100}
          value={draft.organization} onChange={event => change('organization', event.target.value)}
          placeholder={draft.providerKey === 'azure-devops-services' ? 'my-azure-devops-org' : 'my-github-org'} /></label>
        <label>{t('connPat')}<input required autoComplete="off" type="password" maxLength={4096}
          value={draft.personalAccessToken} onChange={event => change('personalAccessToken', event.target.value)}
          placeholder="••••••••••••••••" /></label>
        <div className="form-actions">
          <button className="secondary-button" disabled={busy} onClick={testConnection} type="button">
            {busy ? t('connChecking') : t('connTest')}
          </button>
          <button className="primary" disabled={busy} type="submit">{t('connSave')}</button>
        </div>
      </form>
      {validation && <div className="connection-message" role="status">
        <strong>{validation.isValid ? t('connVerified') : t('connInvalid')}</strong>
        {validation.isValid && <span>{validation.permissionAssessment === 'organization_write_unverified' ?
          t('connWriteWarning') : t('connReadInfo')}</span>}
      </div>}
      <div className="connection-hint"><KeyRound size={14} /> {t('connNoTokens')}</div>
      <div className="connection-hint">{t('connNotSupported')}</div>
    </section>
    <section className="connection-list" aria-label={t('connSaved')}>
      <h2>{t('connSaved')}</h2>
      <p>{t('connIntro')}</p>
      {error && <div className="alert" role="alert">{error}</div>}
      {feedback && <div className="notice" role="status">{feedback}</div>}
      {loading ? <p>{t('connWorking')}</p> : connections.length === 0
        ? <div className="connection-empty"><Cloud size={33} /><h3>{t('connEmpty')}</h3><p>{t('connEmptyText')}</p></div>
        : connections.map(item => <article key={item.id} className="connection-entry">
            <div className="connection-entry-head">
              <div>
                <div className="connection-label">{item.direction === 'source' ? t('connSource') : t('connDestination')}</div>
                <h3>{item.name}</h3>
                <p><GitBranch size={13} /> {item.providerKey} · {item.organization}</p>
                {item.authenticatedAs && <code>{t('connIdentity')}: {item.authenticatedAs}</code>}
              </div>
              <span className={'connection-badge' + (item.validationCode === 'Verified' ? '' : ' invalid')}>
                {item.validationCode === 'Verified' ? <CheckCircle2 size={15} /> : <ShieldAlert size={15} />}
                {item.validationCode === 'Verified' ? t('connVerified') : item.validationCode}
              </span>
            </div>
            <p>{item.permissionAssessment === 'organization_write_unverified' ? t('connWriteWarning') : t('connReadInfo')}</p>
            <div className="connection-actions">
              <button className="secondary-button" type="button" disabled={busyId !== null}
                onClick={() => { void verify(item); }}><RefreshCw size={13} /> {t('connVerify')}</button>
              <button className="text-button" type="button" disabled={busyId !== null}
                onClick={() => { setRotationId(item.id); setReplacementPat(''); }}>{t('connRotate')}</button>
              <button className="text-button danger" type="button" disabled={busyId !== null}
                onClick={() => { void remove(item); }}><Trash2 size={13} /> {t('connDelete')}</button>
            </div>
            {rotationId === item.id && <div className="connection-pat-rotate">
              <label>{t('connPat')}<input type="password" autoComplete="off" value={replacementPat}
                onChange={event => setReplacementPat(event.target.value)} /></label>
              <div className="connection-actions">
                <button className="primary" disabled={busyId !== null || !replacementPat} onClick={() => { void rotate(item); }}>
                  {t('connRotate')}
                </button>
                <button className="secondary-button" onClick={() => { setRotationId(null); setReplacementPat(''); }}>{t('connCancel')}</button>
              </div>
            </div>}
          </article>)}
    </section>
  </div>;
}
