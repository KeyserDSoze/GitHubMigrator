import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';

const translations = {
  en: {
    translation: {
      tagline: 'Migration Workbench', welcome: 'Welcome back', intro: 'Sign in to your migration workspace.',
      username: 'Username', password: 'Password', signIn: 'Sign in', signingIn: 'Signing in…',
      changePassword: 'Set your new password', changeIntro: 'Your account has a temporary password. Update it to continue.',
      currentPassword: 'Current password', newPassword: 'New password', savePassword: 'Save password',
      signOut: 'Sign out', overview: 'Overview', connections: 'Connections', identities: 'Identity mapping',
      repositories: 'Repositories', runs: 'Migration runs', users: 'Users', settings: 'Settings',
      workspace: 'Workspace', readyToStart: 'Your migration starts here',
      description: 'Connect a source, discover projects and identities, then preview and approve changes before any code is moved.',
      stage1: 'Connect systems', stage2: 'Discover and map', stage3: 'Migrate and verify',
      stage1Text: 'Azure DevOps and GitHub Enterprise credentials', stage2Text: 'Projects, access, identities and target names',
      stage3Text: 'Version history, branches and verifiable reports',
      nextStep: 'Planned for the next feature', foundational: 'Foundation environment',
      providerReady: 'Provider contracts defined', integrationsPending: 'Real connectors are not implemented yet.',
      noConnections: 'No source connections yet', noConnectionsText: 'Source discovery will be added as a separate feature. Credentials will be stored outside migration snapshots.',
      noIdentities: 'No identity mapping yet', noIdentitiesText: 'Later you will map Azure DevOps/TFS users to GitHub logins and review orphan access.',
      noRepositories: 'No discovered repositories', noRepositoriesText: 'After discovery you will select repositories, rename targets and choose a history strategy.',
      noRuns: 'No migration jobs', noRunsText: 'Migration execution will require approval, resumable workers and verification.',
      addUser: 'Create platform user', role: 'Role', temporaryPassword: 'Temporary password',
      createUser: 'Create user', userCreated: 'User created. They must change their temporary password on first login.',
      session: 'Platform session', authError: 'Sign-in failed. Check credentials and try again.',
      operationError: 'The operation failed. Please check your input and try again.',
      theme: 'Switch theme', language: 'Language', safe: 'No automatic writes to GitHub',
      status: 'Status', planned: 'Planned', active: 'Active', account: 'Account',
      userInfo: 'Manage platform accounts. External user mapping is a separate feature.',
      accessDenied: 'Only administrators can manage platform users.'
    }
  },
  it: {
    translation: {
      tagline: 'Console di migrazione', welcome: 'Bentornato', intro: 'Accedi alla tua console di migrazione.',
      username: 'Nome utente', password: 'Password', signIn: 'Accedi', signingIn: 'Accesso…',
      changePassword: 'Imposta la nuova password', changeIntro: 'La password del tuo account è provvisoria. Cambiala per continuare.',
      currentPassword: 'Password attuale', newPassword: 'Nuova password', savePassword: 'Salva password',
      signOut: 'Esci', overview: 'Panoramica', connections: 'Connessioni', identities: 'Mapping utenti',
      repositories: 'Repository', runs: 'Esecuzioni', users: 'Utenti', settings: 'Impostazioni',
      workspace: 'Area di lavoro', readyToStart: 'La tua migrazione inizia qui',
      description: 'Collega una sorgente, scopri progetti e utenti, quindi verifica e approva le modifiche prima di spostare il codice.',
      stage1: 'Collega i sistemi', stage2: 'Scopri e mappa', stage3: 'Migra e verifica',
      stage1Text: 'Credenziali Azure DevOps e GitHub Enterprise', stage2Text: 'Progetti, permessi, utenti e nomi destinazione',
      stage3Text: 'Storico, branch e report verificabili',
      nextStep: 'Previsto per la prossima feature', foundational: 'Ambiente iniziale',
      providerReady: 'Contratti dei provider definiti', integrationsPending: 'I connettori reali non sono ancora implementati.',
      noConnections: 'Nessuna connessione sorgente', noConnectionsText: 'La discovery arriverà in una feature dedicata. Le credenziali saranno conservate fuori dagli snapshot.',
      noIdentities: 'Nessun mapping utenti', noIdentitiesText: 'Potrai associare gli utenti Azure DevOps/TFS ai login GitHub e individuare i permessi orfani.',
      noRepositories: 'Nessun repository scoperto', noRepositoriesText: 'Dopo la discovery sceglierai repository, nomi target e strategia dello storico.',
      noRuns: 'Nessuna migrazione', noRunsText: 'L’esecuzione richiederà approvazione, worker riprendibili e verifica.',
      addUser: 'Crea utente piattaforma', role: 'Ruolo', temporaryPassword: 'Password provvisoria',
      createUser: 'Crea utente', userCreated: 'Utente creato. Dovrà cambiare la password al primo accesso.',
      session: 'Sessione piattaforma', authError: 'Accesso non riuscito. Controlla le credenziali.',
      operationError: 'Operazione fallita. Controlla i dati e riprova.',
      theme: 'Cambia tema', language: 'Lingua', safe: 'Nessuna scrittura automatica su GitHub',
      status: 'Stato', planned: 'Previsto', active: 'Attivo', account: 'Account',
      userInfo: 'Gestisci gli account della piattaforma. Il mapping degli utenti esterni è una feature separata.',
      accessDenied: 'Solo gli amministratori possono gestire gli utenti.'
    }
  }
};

void i18n.use(initReactI18next).init({
  resources: translations,
  lng: localStorage.getItem('language') === 'en' ? 'en' : 'it',
  fallbackLng: 'en',
  interpolation: { escapeValue: false }
});
export default i18n;
