import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Bot, CheckCircle2, RefreshCw, ShieldAlert, Trash2 } from 'lucide-react';
import { api, type AiAuthMode, type AiConnection, type AiConnectionDraft, type AiOptions, type AiProviderKey } from './api';

type Form = {
  name: string; provider: AiProviderKey; endpoint: string; model: string; auth: AiAuthMode; apiKey: string;
  apiVersion: string; tokenParameter: string; maxOutputTokens: string; sendTemperature: boolean; temperature: string; timeoutSeconds: string;
};

const emptyForm: Form = {
  name: '', provider: 'OpenAICompatible', endpoint: '', model: '', auth: 'ApiKey', apiKey: '',
  apiVersion: '', tokenParameter: '', maxOutputTokens: '', sendTemperature: true, temperature: '', timeoutSeconds: ''
};

const failureKeys: Record<string, string> = {
  Unauthorized: 'aiErrUnauthorized', ModelNotFound: 'aiErrModelNotFound', RateLimited: 'aiErrRateLimited',
  Timeout: 'aiErrTimeout', Unavailable: 'aiErrUnavailable', MalformedResponse: 'aiErrMalformed',
  UnsupportedParameter: 'aiErrUnsupportedParameter', EndpointNotAllowed: 'aiErrEndpointNotAllowed',
  CredentialMissing: 'aiErrCredentialMissing', NotVerified: 'aiErrNotVerified', InvalidAiConnectionInput: 'aiErrInvalid',
  ContentFiltered: 'aiErrContentFiltered', ContextLengthExceeded: 'aiErrContext', Misconfigured: 'aiErrInvalid'
};

function explain(code: string, t: (key: string) => string): string {
  return t(failureKeys[code] ?? 'aiErrDefault');
}

function toDraft(form: Form): AiConnectionDraft {
  const draft: AiConnectionDraft = {
    name: form.name, provider: form.provider, endpoint: form.endpoint, model: form.model, auth: form.auth
  };
  if (form.auth === 'ApiKey') draft.apiKey = form.apiKey;
  if (form.provider === 'AzureOpenAI' && form.apiVersion.trim()) draft.apiVersion = form.apiVersion.trim();
  if (form.tokenParameter) draft.tokenParameter = form.tokenParameter;
  if (form.maxOutputTokens) draft.maxOutputTokens = Number(form.maxOutputTokens);
  if (!form.sendTemperature) draft.sendTemperature = false;
  else if (form.temperature) draft.temperature = Number(form.temperature);
  if (form.timeoutSeconds) draft.timeoutSeconds = Number(form.timeoutSeconds);
  return draft;
}

export default function AiConnectionsPanel({ token, isSuperAdmin }: { token: string; isSuperAdmin: boolean }) {
  const { t } = useTranslation();
  const [options, setOptions] = useState<AiOptions | null>(null);
  const [items, setItems] = useState<AiConnection[]>([]);
  const [form, setForm] = useState<Form>(emptyForm);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [rotationId, setRotationId] = useState<string | null>(null);
  const [replacementKey, setReplacementKey] = useState('');
  const [tested, setTested] = useState<string | null>(null);
  const [feedback, setFeedback] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    let current = true;
    Promise.all([api.aiOptions(token), api.listAiConnections(token)])
      .then(([opts, list]) => { if (current) { setOptions(opts); setItems(list); } })
      .catch(() => { if (current) setError(t('operationError')); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, t]);

  const change = <K extends keyof Form>(field: K, value: Form[K]) => {
    setForm(current => {
      const next = { ...current, [field]: value };
      if (field === 'provider' && value !== 'AzureOpenAI') {
        next.apiVersion = '';
        if (next.auth === 'ManagedIdentity') next.auth = 'ApiKey';
      }
      return next;
    });
    setTested(null); setError(''); setFeedback('');
  };

  async function testModel() {
    setBusy(true); setError(''); setFeedback(''); setTested(null);
    try {
      const result = await api.validateAiConnection(token, toDraft(form));
      if (result.isValid) setTested(result.model ?? form.model);
      else setError(explain(result.code, t));
    } catch (reason) { setError(explain(reason instanceof Error ? reason.message : '', t)); }
    finally { setBusy(false); }
  }

  async function save(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(''); setFeedback('');
    try {
      const item = await api.saveAiConnection(token, toDraft(form));
      setItems(current => [...current, item]);
      setForm(emptyForm); setTested(null); setFeedback(t('aiCreated'));
    } catch (reason) { setError(explain(reason instanceof Error ? reason.message : '', t)); }
    finally { setBusy(false); }
  }

  const replace = (next: AiConnection) => setItems(current => current.map(c => c.id === next.id ? next : c));

  async function verify(item: AiConnection) {
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      const next = await api.verifyAiConnection(token, item.id);
      replace(next);
      if (next.verificationCode !== 'Verified') setError(explain(next.verificationCode, t));
    } catch { setError(t('operationError')); }
    finally { setBusyId(null); }
  }

  async function rotate(item: AiConnection) {
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      replace(await api.rotateAiCredential(token, item.id, replacementKey));
      setRotationId(null); setReplacementKey(''); setFeedback(t('aiRotated'));
    } catch (reason) { setError(explain(reason instanceof Error ? reason.message : '', t)); }
    finally { setBusyId(null); }
  }

  async function consent(item: AiConnection, allow: boolean) {
    if (allow && !window.confirm(t('aiConsentConfirm'))) return;
    setBusyId(item.id); setError(''); setFeedback('');
    try { replace(await api.setAiSourceConsent(token, item.id, allow)); }
    catch (reason) { setError(explain(reason instanceof Error ? reason.message : '', t)); }
    finally { setBusyId(null); }
  }

  async function remove(item: AiConnection) {
    if (!window.confirm(t('aiRemoveConfirm'))) return;
    setBusyId(item.id); setError(''); setFeedback('');
    try {
      await api.deleteAiConnection(token, item.id);
      setItems(current => current.filter(c => c.id !== item.id));
    } catch { setError(t('operationError')); }
    finally { setBusyId(null); }
  }

  const providerLabel = (provider: AiProviderKey) => provider === 'AzureOpenAI' ? t('aiProviderAzure') : t('aiProviderCompat');

  return <div className="connection-layout ai-connections">
    <section className="connection-form" aria-label={t('aiNew')}>
      <h2><Bot size={18} /> {t('aiNew')}</h2>
      <p>{t('aiIntro')}</p>
      <form onSubmit={save}>
        <label>{t('aiProvider')}
          <select aria-label={t('aiProvider')} value={form.provider} onChange={event => change('provider', event.target.value as AiProviderKey)}>
            <option value="OpenAICompatible">{t('aiProviderCompat')}</option>
            <option value="AzureOpenAI">{t('aiProviderAzure')}</option>
          </select>
        </label>
        <label>{t('aiProfileName')}<input required maxLength={120} value={form.name}
          onChange={event => change('name', event.target.value)} placeholder="Corporate model" /></label>
        <label>{t('aiEndpoint')}<input required maxLength={512} inputMode="url" value={form.endpoint}
          onChange={event => change('endpoint', event.target.value)}
          placeholder={form.provider === 'AzureOpenAI' ? 'https://my-resource.openai.azure.com' : 'https://api.openai.com/v1'} /></label>
        <label>{t('aiModel')}<input required maxLength={100} value={form.model}
          onChange={event => change('model', event.target.value)} placeholder="gpt-4.1" /></label>
        <label>{t('aiAuth')}
          <select aria-label={t('aiAuth')} value={form.auth} onChange={event => change('auth', event.target.value as AiAuthMode)}>
            <option value="ApiKey">{t('aiAuthKey')}</option>
            <option value="None">{t('aiAuthNone')}</option>
            {form.provider === 'AzureOpenAI' && options?.managedIdentityAvailable && <option value="ManagedIdentity">{t('aiAuthIdentity')}</option>}
          </select>
        </label>
        {form.auth === 'ApiKey' && <label>{t('aiApiKey')}<input required autoComplete="off" type="password" maxLength={4096}
          value={form.apiKey} onChange={event => change('apiKey', event.target.value)} placeholder="••••••••••••••••" /></label>}
        {form.provider === 'AzureOpenAI' && <label>{t('aiApiVersion')}<input maxLength={32} value={form.apiVersion}
          onChange={event => change('apiVersion', event.target.value)} placeholder="2024-10-21" /></label>}
        <details>
          <summary>{t('aiAdvanced')}</summary>
          <label>{t('aiMaxTokens')}<input type="number" min={16} max={128000} value={form.maxOutputTokens}
            onChange={event => change('maxOutputTokens', event.target.value)} placeholder="4096" /></label>
          <label>{t('aiTokenParam')}
            <select aria-label={t('aiTokenParam')} value={form.tokenParameter} onChange={event => change('tokenParameter', event.target.value)}>
              <option value="">{t('aiDefault')}</option>
              <option value="MaxTokens">max_tokens</option>
              <option value="MaxCompletionTokens">max_completion_tokens</option>
            </select>
          </label>
          <label>{t('aiTemperature')}<input type="number" min={0} max={2} step={0.1} disabled={!form.sendTemperature} value={form.temperature}
            onChange={event => change('temperature', event.target.value)} placeholder="0" /></label>
          <label className="checkbox-row"><input type="checkbox" checked={!form.sendTemperature}
            onChange={event => change('sendTemperature', !event.target.checked)} /> {t('aiOmitTemperature')}</label>
          <label>{t('aiTimeout')}<input type="number" min={5} max={600} value={form.timeoutSeconds}
            onChange={event => change('timeoutSeconds', event.target.value)} placeholder="60" /></label>
        </details>
        <div className="form-actions">
          <button className="secondary-button" disabled={busy} onClick={() => { void testModel(); }} type="button">
            {busy ? t('aiTesting') : t('aiTest')}
          </button>
          <button className="primary" disabled={busy} type="submit">{t('aiSave')}</button>
        </div>
      </form>
      {tested && <div className="connection-message" role="status"><strong>{t('aiVerified')}</strong><span>{t('aiAnsweredAs', { model: tested })}</span></div>}
      {options && <div className="connection-hint">{t('aiAllowedHosts', { hosts: options.allowedHosts.join(', ') })}{options.allowHttp ? ` ${t('aiHttpAllowed')}` : ''}</div>}
      <div className="connection-hint">{t('aiNoKeys')}</div>
    </section>
    <section className="connection-list" aria-label={t('aiSaved')}>
      <h2>{t('aiSaved')}</h2>
      {error && <div className="alert" role="alert">{error}</div>}
      {feedback && <div className="notice" role="status">{feedback}</div>}
      {loading ? <p>{t('connWorking')}</p> : items.length === 0
        ? <div className="connection-empty"><Bot size={33} /><h3>{t('aiEmpty')}</h3><p>{t('aiEmptyText')}</p></div>
        : items.map(item => <article key={item.id} className="connection-entry" aria-label={item.name}>
            <div className="connection-entry-head">
              <div>
                <div className="connection-label">{providerLabel(item.provider)}</div>
                <h3>{item.name}</h3>
                <p>{item.model} · {item.endpoint}</p>
                {item.verifiedModel && <code>{t('aiAnsweredAs', { model: item.verifiedModel })}</code>}
              </div>
              <span className={'connection-badge' + (item.verificationCode === 'Verified' ? '' : ' invalid')}>
                {item.verificationCode === 'Verified' ? <CheckCircle2 size={15} /> : <ShieldAlert size={15} />}
                {item.verificationCode === 'Verified' ? t('aiVerified') : item.verificationCode}
              </span>
            </div>
            <p>{item.sendSourceContent
              ? t('aiConsentOn', { who: item.consentBy ?? '?', when: item.consentAtUtc ? new Date(item.consentAtUtc).toLocaleString() : '?' })
              : t('aiConsentOff')}</p>
            <div className="connection-actions">
              <button className="secondary-button" type="button" disabled={busyId !== null}
                onClick={() => { void verify(item); }}><RefreshCw size={13} /> {t('aiVerify')}</button>
              {item.auth === 'ApiKey' && <button className="text-button" type="button" disabled={busyId !== null}
                onClick={() => { setRotationId(item.id); setReplacementKey(''); }}>{t('aiRotate')}</button>}
              {isSuperAdmin
                ? <button className="text-button" type="button" disabled={busyId !== null || (!item.sendSourceContent && item.verificationCode !== 'Verified')}
                    onClick={() => { void consent(item, !item.sendSourceContent); }}>
                    {item.sendSourceContent ? t('aiConsentRevoke') : t('aiConsentAllow')}</button>
                : <span className="connection-hint">{t('aiConsentSuperadmin')}</span>}
              <button className="text-button danger" type="button" disabled={busyId !== null}
                onClick={() => { void remove(item); }}><Trash2 size={13} /> {t('aiDelete')}</button>
            </div>
            {rotationId === item.id && <div className="connection-pat-rotate">
              <label>{t('aiApiKey')}<input type="password" autoComplete="off" value={replacementKey}
                onChange={event => setReplacementKey(event.target.value)} /></label>
              <div className="connection-actions">
                <button className="primary" disabled={busyId !== null || !replacementKey} onClick={() => { void rotate(item); }}>{t('aiRotate')}</button>
                <button className="secondary-button" onClick={() => { setRotationId(null); setReplacementKey(''); }}>{t('connCancel')}</button>
              </div>
            </div>}
          </article>)}
    </section>
  </div>;
}
