import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  ArrowRight, CheckCircle2, ChevronRight, CircleHelp, Cloud, Code2, Database, GitBranch,
  Globe2, KeyRound, Languages, LayoutDashboard, LockKeyhole, LogOut, Moon, Plus, ShieldCheck,
  Sun, Users, Workflow
} from 'lucide-react';
import { api, type Session } from './api';
import ConnectionsPage from './ConnectionsPage';

type Page = 'overview' | 'connections' | 'identities' | 'repositories' | 'runs' | 'users';
const navigation: { id: Page; icon: typeof LayoutDashboard }[] = [
  { id: 'overview', icon: LayoutDashboard }, { id: 'connections', icon: Cloud },
  { id: 'identities', icon: Users }, { id: 'repositories', icon: GitBranch },
  { id: 'runs', icon: Workflow }, { id: 'users', icon: ShieldCheck }
];

function Brand() {
  return <div className="brand"><div className="brand-mark"><GitBranch size={21} strokeWidth={2.4} /></div>
    <div><strong>GitHubMigrator</strong><small>ENTERPRISE WORKBENCH</small></div></div>;
}

function App() {
  const { t, i18n } = useTranslation();
  const [theme, setTheme] = useState(() => localStorage.getItem('theme') === 'light' ? 'light' : 'dark');
  const [session, setSession] = useState<Session | null>(null);
  const [page, setPage] = useState<Page>('overview');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const [newUsername, setNewUsername] = useState('');
  const [temporaryPassword, setTemporaryPassword] = useState('');
  const [newRole, setNewRole] = useState('user');
  const isAdmin = session?.role === 'admin' || session?.role === 'superadmin';

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem('theme', theme);
  }, [theme]);

  const toggleLanguage = () => {
    const language = i18n.language === 'it' ? 'en' : 'it';
    void i18n.changeLanguage(language);
    document.documentElement.lang = language;
    localStorage.setItem('language', language);
  };

  async function signIn(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try { setSession(await api.login(username, password)); setPassword(''); }
    catch { setError(t('authError')); }
    finally { setBusy(false); }
  }

  async function changePassword(event: FormEvent) {
    event.preventDefault(); if (!session) return;
    setBusy(true); setError('');
    try { setSession(await api.changePassword(session.accessToken, currentPassword, newPassword));
      setCurrentPassword(''); setNewPassword('');
    } catch { setError(t('operationError')); }
    finally { setBusy(false); }
  }

  async function createUser(event: FormEvent) {
    event.preventDefault(); if (!session) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await api.createUser(session.accessToken, newUsername, temporaryPassword, newRole);
      setNewUsername(''); setTemporaryPassword(''); setNotice(t('userCreated'));
    } catch { setError(t('operationError')); }
    finally { setBusy(false); }
  }

  const controls = <div className="controls">
    <button className="icon-button" type="button" onClick={toggleLanguage} title={t('language')} aria-label={t('language')}>
      <Languages size={18} /><span>{i18n.language === 'it' ? 'IT' : 'EN'}</span>
    </button>
    <button className="icon-button square" type="button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label={t('theme')} title={t('theme')}>
      {theme === 'dark' ? <Sun size={19} /> : <Moon size={19} />}
    </button>
  </div>;

  if (!session || session.mustChangePassword) {
    return <div className="auth-shell">
      <div className="auth-ambient ambient-one" /><div className="auth-ambient ambient-two" />
      <header className="auth-header"><Brand />{controls}</header>
      <div className="auth-grid">
        <section className="auth-story">
          <div className="eyebrow"><span className="live-dot" /> {t('tagline')}</div>
          <h1>Move the history.<br /><span>Keep the context.</span></h1>
          <p>{t('description')}</p>
          <div className="story-icons"><span><Code2 size={18} /> Azure DevOps</span><ArrowRight size={19} /><span><GitBranch size={18} /> GitHub Enterprise</span></div>
          <div className="story-foot"><ShieldCheck size={16} /> {t('safe')}</div>
        </section>
        <section className="auth-card" aria-label={session?.mustChangePassword ? t('changePassword') : t('signIn')}>
          <div className="auth-icon"><KeyRound size={22} /></div>
          <div className="eyebrow soft">{t('session')}</div>
          <h2>{session?.mustChangePassword ? t('changePassword') : t('welcome')}</h2>
          <p className="muted">{session?.mustChangePassword ? t('changeIntro') : t('intro')}</p>
          {error && <div className="alert" role="alert">{error}</div>}
          {session?.mustChangePassword
            ? <form onSubmit={changePassword}>
                <label>{t('currentPassword')}<input autoComplete="current-password" type="password" required minLength={8} value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} /></label>
                <label>{t('newPassword')}<input autoComplete="new-password" type="password" required minLength={8} value={newPassword} onChange={e => setNewPassword(e.target.value)} /></label>
                <button className="primary" disabled={busy} type="submit">{t('savePassword')}<ArrowRight size={17} /></button>
              </form>
            : <form onSubmit={signIn}>
                <label>{t('username')}<input autoComplete="username" required value={username} onChange={e => setUsername(e.target.value)} placeholder="superadmin" /></label>
                <label>{t('password')}<input autoComplete="current-password" type="password" required value={password} onChange={e => setPassword(e.target.value)} placeholder="••••••••" /></label>
                <button className="primary" disabled={busy} type="submit">{busy ? t('signingIn') : t('signIn')}<ArrowRight size={17} /></button>
              </form>}
          <p className="auth-caption"><LockKeyhole size={14} /> Credentials are handled by the API; use HTTPS in production</p>
        </section>
      </div>
      <footer className="auth-footer">GitHubMigrator · Foundation v0.1</footer>
    </div>;
  }

  const placeholders: Record<Exclude<Page, 'overview' | 'users'>, { title: string; body: string; icon: typeof Cloud }> = {
    connections: { title: t('noConnections'), body: t('noConnectionsText'), icon: Cloud },
    identities: { title: t('noIdentities'), body: t('noIdentitiesText'), icon: Users },
    repositories: { title: t('noRepositories'), body: t('noRepositoriesText'), icon: GitBranch },
    runs: { title: t('noRuns'), body: t('noRunsText'), icon: Workflow }
  };
  const tiles = [
    { icon: Cloud, title: t('stage1'), text: t('stage1Text'), index: '01' },
    { icon: Users, title: t('stage2'), text: t('stage2Text'), index: '02' },
    { icon: GitBranch, title: t('stage3'), text: t('stage3Text'), index: '03' }
  ];

  return <div className="app-shell">
    <aside className="sidebar">
      <Brand />
      <div className="section-caption">{t('workspace')}</div>
      <nav aria-label="Main navigation">{navigation.map(item => {
        const Icon = item.icon;
        return <button key={item.id} className={'nav-link ' + (page === item.id ? 'selected' : '')} onClick={() => { setPage(item.id); setError(''); setNotice(''); }} type="button">
          <Icon size={19} /><span>{t(item.id)}</span>{page === item.id && <ChevronRight size={16} className="nav-chevron" />}
        </button>;
      })}</nav>
      <div className="sidebar-bottom"><div className="secure-label"><ShieldCheck size={17} /> {t('safe')}</div>
        <button className="sidebar-user" type="button" onClick={() => { setSession(null); setPage('overview'); }}>
          <span className="avatar">{session.username.slice(0, 2).toUpperCase()}</span><span><strong>{session.username}</strong><small>{session.role}</small></span><LogOut size={17} />
        </button>
      </div>
    </aside>
    <main className="main">
      <header className="topbar"><div className="breadcrumbs">{t('workspace')} <ChevronRight size={15} /> <strong>{t(page)}</strong></div>{controls}</header>
      <div className="content">
        <div className="page-heading"><div className="eyebrow"><span className="live-dot" /> {t('foundational')}</div>
          <h1>{page === 'overview' ? t('readyToStart') : t(page)}</h1>
          <p>{page === 'overview' ? t('description') : page === 'users' ? t('userInfo') : t('integrationsPending')}</p>
        </div>
        {page === 'overview' && <>
          <div className="hero-banner"><div><span className="hero-label"><ShieldCheck size={16} /> PLATFORM READY · MIGRATION PLANNED</span>
            <h2>From legacy source<br />to GitHub Enterprise.</h2>
            <p>{t('description')}</p><div className="hero-pill"><CheckCircle2 size={16} /> {t('providerReady')}</div></div>
            <div className="hero-art" aria-hidden="true"><span className="orb orb-a"><Database size={34} /></span><span className="orb orb-b"><GitBranch size={42} /></span><span className="orb-link" /></div>
          </div>
          <div className="section-header"><h2>Migration journey</h2><span>{t('nextStep')}</span></div>
          <div className="step-grid">{tiles.map(tile => { const Icon = tile.icon; return <article className="step-card" key={tile.index}>
            <div className="step-top"><span className="step-icon"><Icon size={21} /></span><span>{tile.index} / 03</span></div>
            <h3>{tile.title}</h3><p>{tile.text}</p><div className="card-foot">{t('planned')} <CircleHelp size={15} /></div>
          </article>; })}</div>
        </>}
        {page === 'connections' && <ConnectionsPage token={session.accessToken} isAdmin={isAdmin} />}
        {page !== 'overview' && page !== 'users' && page !== 'connections' && (() => {
          const placeholder = placeholders[page]; const Icon = placeholder.icon;
          return <section className="empty-card"><div className="empty-icon"><Icon size={29} /></div><h2>{placeholder.title}</h2>
            <p>{placeholder.body}</p><span className="subtle-tag">{t('planned')}</span></section>;
        })()}
        {page === 'users' && <section className="user-card">
          {!isAdmin ? <div className="alert">{t('accessDenied')}</div> : <>
            <div className="user-card-head"><div className="empty-icon small"><Plus size={22} /></div><div><h2>{t('addUser')}</h2><p>{t('userInfo')}</p></div></div>
            {error && <div className="alert" role="alert">{error}</div>}
            {notice && <div className="notice" role="status">{notice}</div>}
            <form className="user-form" onSubmit={createUser}>
              <label>{t('username')}<input required value={newUsername} onChange={e => setNewUsername(e.target.value)} /></label>
              <label>{t('temporaryPassword')}<input required minLength={8} type="password" autoComplete="new-password" value={temporaryPassword} onChange={e => setTemporaryPassword(e.target.value)} /></label>
              <label>{t('role')}<select value={newRole} onChange={e => setNewRole(e.target.value)}>
                <option value="user">user</option><option value="admin" disabled={session.role !== 'superadmin'}>admin</option>
                <option value="superadmin" disabled={session.role !== 'superadmin'}>superadmin</option>
              </select></label>
              <button className="primary" disabled={busy} type="submit">{t('createUser')}<Plus size={17} /></button>
            </form>
          </>}
        </section>}
        <footer className="content-footer"><span>GitHubMigrator · v0.1</span><span><Globe2 size={15} /> GitHub Enterprise <span className="footer-divider">•</span> <CheckCircle2 size={15} /> {t('safe')}</span></footer>
      </div>
    </main>
  </div>;
}
export default App;
