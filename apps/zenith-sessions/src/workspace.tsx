import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { DestinationPicker, type Destination } from './destination-picker';
import { Icon, type IconName } from './design-icons';
import './zenith-design.css';
import './workspace.css';

type Account = { profileId: string; username: string; userId: number };
type Phase = 'preparing' | 'starting' | 'running' | 'unconfirmed' | 'stopping' | 'stopped' | 'failed';
type Session = {
  attemptId: string; accountId: string; username: string; placeId: string;
  phase: Phase; pid: number | null; requestedServer: string | null; observedServer: string | null; detail: string | null;
};
type Admission = {
  limitingResource: 'stale' | 'invalid' | 'commit' | 'physical_memory' | null;
  physicalAvailable: number; physicalRequired: number;
  commitAvailable: number; commitRequired: number; canTryOne: boolean;
};
type Snapshot = { accounts: Account[]; session: Session | null; admission: Admission | null; signInAttempt: string | null };
type Page = 'Overview' | 'Accounts' | 'Sessions';

const pages: [Page, IconName][] = [['Overview', 'grid'], ['Accounts', 'users'], ['Sessions', 'window']];
const themes = [
  ['graphite-mint', 'Graphite Mint'], ['midnight', 'Midnight'], ['amber-night', 'Amber Night'],
  ['ocean-blue', 'Ocean Blue'], ['violet-dusk', 'Violet Dusk'], ['light', 'Light'], ['follow-windows', 'Follow Windows'],
] as const;
const phaseLabel: Record<Phase, string> = {
  preparing: 'Preparing', starting: 'Starting Roblox', running: 'Arrival confirmed',
  unconfirmed: 'Running · unconfirmed', stopping: 'Stopping', stopped: 'Stopped', failed: 'Launch failed',
};
const phaseTone: Record<Phase, string> = {
  preparing: 'launching', starting: 'launching', running: 'online',
  unconfirmed: 'attention', stopping: 'launching', stopped: 'idle', failed: 'attention',
};
const holds: Record<NonNullable<Admission['limitingResource']>, string> = {
  stale: 'The memory reading expired.', invalid: 'Windows reported an invalid memory reading.',
  commit: 'Waiting for commit headroom.', physical_memory: 'Waiting for physical memory headroom.',
};

function gib(bytes: number) { return `${(bytes / 2 ** 30).toFixed(2)} GiB`; }
function isBusy(session: Session | null | undefined) { return Boolean(session && session.phase !== 'stopped' && session.phase !== 'failed'); }

function Status({ session }: { session?: Session | null }) {
  return <span className={`status status-${session ? phaseTone[session.phase] : 'idle'}`}><i />{session ? phaseLabel[session.phase] : 'Not running'}</span>;
}

function Memory({ admission }: { admission: Admission | null | undefined }) {
  return admission ? <section className="account-panel preview-panel">
    <h2>Launch admission</h2>
    <p>{admission.limitingResource ? holds[admission.limitingResource] : 'Memory headroom is available.'}</p>
    <dl className="memory-grid">
      <dt>Physical memory</dt><dd>{gib(admission.physicalAvailable)} available · {gib(admission.physicalRequired)} required</dd>
      <dt>Commit</dt><dd>{gib(admission.commitAvailable)} available · {gib(admission.commitRequired)} required</dd>
    </dl>
  </section> : <p>Windows memory reading unavailable.</p>;
}

function App() {
  const [page, setPage] = useState<Page>('Accounts');
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [selected, setSelected] = useState('');
  const [query, setQuery] = useState('');
  const [destination, setDestination] = useState<Destination>(() => ({
    place: localStorage.getItem('zenith-preview-place') ?? '', name: '', job: null,
  }));
  const [theme, setTheme] = useState(() => {
    const saved = localStorage.getItem('zenith-theme');
    return themes.find(([id]) => id === saved)?.[0] ?? 'graphite-mint';
  });
  const [showAppearance, setShowAppearance] = useState(false);
  const [failure, setFailure] = useState('');
  const [loadingError, setLoadingError] = useState(false);
  const [launching, setLaunching] = useState(false);
  const [importing, setImporting] = useState(false);
  const [signInBusy, setSignInBusy] = useState(false);
  const [remove, setRemove] = useState<Account | null>(null);
  const file = useRef<HTMLInputElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const reading = useRef(false);
  const accounts = snapshot?.accounts ?? [];
  const account = accounts.find(value => value.profileId === selected) ?? (accounts.length === 1 ? accounts[0] : undefined);
  const session = snapshot?.session;
  const admission = snapshot?.admission;
  const signInAttempt = snapshot?.signInAttempt;
  const busy = isBusy(session);
  const filtered = accounts.filter(item => item.username.toLowerCase().includes(query.trim().toLowerCase()));
  const placeEntered = Boolean(destination.place.trim());

  async function refresh() {
    if (reading.current) return;
    reading.current = true;
    try {
      setSnapshot(await invoke<Snapshot>('workspace_snapshot'));
      setLoadingError(false);
    } catch {
      setLoadingError(true);
    } finally { reading.current = false; }
  }

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), 1000);
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    const listener = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setPage('Accounts');
        window.requestAnimationFrame(() => search.current?.focus());
      }
    };
    window.addEventListener('keydown', listener);
    return () => window.removeEventListener('keydown', listener);
  }, []);
  useEffect(() => localStorage.setItem('zenith-theme', theme), [theme]);

  async function act(command: string, payload: Record<string, unknown>) {
    setFailure('');
    try {
      await invoke(command, payload);
      return true;
    } catch (error) {
      setFailure(typeof error === 'string' ? error : 'Zenith could not complete the request.');
      return false;
    } finally { void refresh(); }
  }

  async function importFile(input: HTMLInputElement) {
    const selectedFile = input.files?.[0];
    input.value = '';
    if (!selectedFile) return;
    if (selectedFile.size === 0 || selectedFile.size > 131072) {
      setFailure('Choose a protected Zenith sign-in bundle smaller than 128 KiB.');
      return;
    }
    setImporting(true);
    setFailure('');
    try {
      const encrypted = Array.from(new Uint8Array(await selectedFile.arrayBuffer()));
      const imported = await invoke<Account>('import_account', { encrypted });
      setSelected(imported.profileId);
    } catch (error) {
      setFailure(typeof error === 'string' ? error : 'Could not read the protected sign-in bundle.');
    } finally {
      setImporting(false);
      void refresh();
    }
  }

  async function launch(tryOne: boolean) {
    if (!account || !placeEntered) return;
    setLaunching(true);
    try {
      if (await act('launch_account', { id: account.profileId, placeId: destination.place, job: destination.job, tryOne })) {
        localStorage.setItem('zenith-preview-place', destination.place.trim());
      }
    }
    finally { setLaunching(false); }
  }

  async function beginSignIn() {
    setSignInBusy(true);
    try { await act('begin_sign_in', {}); }
    finally { setSignInBusy(false); }
  }

  async function saveSignIn() {
    if (!signInAttempt) return;
    setSignInBusy(true);
    setFailure('');
    try {
      const account = await invoke<Account>('finish_sign_in', { attempt: signInAttempt });
      setSelected(account.profileId);
    } catch (error) {
      setFailure(typeof error === 'string' ? error : 'Could not save the Roblox sign-in.');
    } finally { setSignInBusy(false); void refresh(); }
  }

  function openLaunch(profile: Account) {
    setSelected(profile.profileId);
    setPage('Sessions');
  }

  return <main className="app-shell" data-theme={theme}>
    <header className="topbar">
      <div className="brand-zone">
        <button className="brand" onClick={() => setPage('Overview')} aria-label="Zenith home">
          <span className="brand-mark">Z</span><span>Zenith</span>
        </button>
        <span className="brand-stat">{accounts.length} {accounts.length === 1 ? 'account' : 'accounts'}</span>
      </div>
      <nav className="nav-pill" aria-label="Primary navigation">
        {pages.map(([label, icon]) => <button key={label} aria-label={label} aria-current={page === label ? 'page' : undefined}
          className={page === label ? 'active' : ''} onClick={() => setPage(label)}><Icon name={icon} size={16}/><span>{label}</span></button>)}
      </nav>
      <div className="top-actions">
        <button className="icon-button" aria-label="Refresh" onClick={() => void refresh()}><Icon name="refresh" size={16}/></button>
        <button className="icon-button" aria-label="Appearance" aria-expanded={showAppearance} onClick={() => setShowAppearance(value => !value)}><Icon name="sun" size={16}/></button>
      </div>
    </header>
    <section className="workspace">
      {showAppearance && <section className="account-panel preview-panel appearance-preview">
        <label>Appearance <select value={theme} onChange={event => {
          const choice = themes.find(([id]) => id === event.target.value);
          if (choice) setTheme(choice[0]);
        }}>{themes.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      </section>}
      <div className="page-heading">
        <div><h1>{page}</h1><p>{page === 'Accounts' ? 'Manage your saved Roblox sign-ins.' : page === 'Sessions' ? 'Choose a saved account and a Roblox place.' : 'Review your local workspace and launch capacity.'}</p></div>
        {page === 'Accounts' && <div className="heading-actions">
          <button className="secondary-button" disabled={importing} onClick={() => file.current?.click()}>{importing ? 'Importing…' : 'Import saved sign-in'}</button>
          <button className="primary-button" disabled={signInBusy || !!signInAttempt} onClick={() => void beginSignIn()}><Icon name="plus" size={16}/> Add account</button>
        </div>}
      </div>
      <input ref={file} type="file" accept=".dpapi" hidden onChange={event => void importFile(event.currentTarget)}/>
      {loadingError && <p role="alert" className="inline-alert">Zenith could not read the local workspace. Select Refresh to retry.</p>}
      {failure && <p role="alert" className="inline-alert">{failure}</p>}
      {signInAttempt && <section className="account-panel preview-panel" aria-label="Roblox sign-in">
        <h2>Roblox sign-in</h2>
        <p>Finish signing in inside the Roblox window. Return here when Roblox shows your account, then save the sign-in.</p>
        <div className="row-actions">
          <button className="primary-button" disabled={signInBusy} onClick={() => void saveSignIn()}>{signInBusy ? 'Checking sign-in…' : 'Save sign-in'}</button>
          <button className="secondary-button" onClick={() => void act('cancel_sign_in', { attempt: signInAttempt })}>Cancel sign-in</button>
        </div>
      </section>}
      {page === 'Overview' && <>
        <div className="metrics-grid preview-metrics">
          <article className="metric-card"><div className="metric-top">Saved accounts</div><strong>{accounts.length}</strong></article>
          <article className="metric-card"><div className="metric-top">Owned session</div><Status session={session}/></article>
          <article className="metric-card"><div className="metric-top">Available memory</div><strong>{admission ? gib(admission.physicalAvailable) : 'Unavailable'}</strong></article>
        </div>
        <Memory admission={admission}/>
      </>}
      {page === 'Accounts' && <section className="account-panel">
        <div className="panel-heading"><div><h2>Account library</h2><p>{filtered.length} of {accounts.length} accounts shown</p></div></div>
        <div className="toolbar"><label className="search-box"><Icon name="search" size={17}/><input ref={search} value={query} onChange={event => setQuery(event.target.value)} placeholder="Search accounts…" aria-label="Search accounts"/><kbd>Ctrl K</kbd></label></div>
        {filtered.length ? <div className="table-wrap"><table>
          <thead><tr><th>Account</th><th>Status</th><th>Actions</th></tr></thead>
          <tbody>{filtered.map(item => <tr key={item.profileId} className={item.profileId === account?.profileId ? 'selected-row' : ''}>
            <td><div className="account-cell"><span className="avatar preview-avatar">{item.username.slice(0, 2).toUpperCase()}</span><div><strong>{item.username}</strong><span>@{item.username}</span></div></div></td>
            <td><Status session={item.profileId === session?.accountId ? session : null}/></td>
            <td><div className="row-actions"><button className="launch-button" onClick={() => openLaunch(item)}><Icon name="play" size={14}/> Select for launch</button>
              <button className="secondary-button" disabled={busy && item.profileId === session?.accountId} onClick={() => setRemove(item)}>Remove</button></div></td>
          </tr>)}</tbody>
        </table></div> : <div className="empty-state"><strong>{accounts.length ? 'No matching accounts' : 'No saved sign-ins'}</strong>
          <span>{accounts.length ? 'Try a different search.' : 'Add an account through Roblox or import a protected bundle from Zenith.'}</span></div>}
      </section>}
      {page === 'Sessions' && <>
        <section className="account-panel preview-panel">
          <h2>Launch Roblox</h2>
          <div className="session-fields">
            <label>Account <select value={account?.profileId ?? ''} disabled={busy || launching} onChange={event => setSelected(event.target.value)}>
              <option value="" disabled>Choose an account</option>{accounts.map(item => <option key={item.profileId} value={item.profileId}>{item.username}</option>)}
            </select></label>
          </div>
          <DestinationPicker destination={destination} onChange={setDestination} disabled={busy || launching}/>
          <div className="row-actions"><button className="primary-button" disabled={!account || !placeEntered || busy || launching || !admission || !!admission.limitingResource} onClick={() => void launch(false)}><Icon name="play" size={15}/> Launch</button>
            {admission?.canTryOne && <button className="secondary-button" disabled={!account || !placeEntered || busy || launching} onClick={() => void launch(true)}>Try one account anyway</button>}
          </div>
        </section>
        {session && <section className="account-panel preview-panel session-status" aria-label="Current session">
          <div className="session-title"><h2>{session.username}</h2><Status session={session}/></div>
          <p>Place {session.placeId}{session.pid ? ` · PID ${session.pid}` : ''}</p>
          {session.requestedServer && <p>Requested server {session.requestedServer}</p>}
          {session.detail && <p>{session.detail}</p>}
          {session.observedServer && <p>Confirmed initial server {session.observedServer}</p>}
          {busy && <div className="row-actions">
            {session.phase === 'running' && <button className="secondary-button" onClick={() => void act('focus_session', { attempt: session.attemptId })}>Focus Roblox</button>}
            <button className="secondary-button" disabled={session.phase === 'stopping'} onClick={() => void act('stop_session', { attempt: session.attemptId })}>{session.phase === 'preparing' ? 'Cancel launch' : 'Stop Roblox'}</button>
          </div>}
        </section>}
        <Memory admission={admission}/>
      </>}
    </section>
    {remove && <div className="modal-backdrop"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="remove-heading">
      <h2 id="remove-heading">Remove {remove.username}?</h2><p>This removes the sign-in from this preview library.</p>
      <div className="modal-actions"><button className="secondary-button" onClick={() => setRemove(null)}>Cancel</button>
        <button className="primary-button" onClick={() => { const id = remove.profileId; setRemove(null); void act('remove_account', { id }); }}>Remove</button></div>
    </section></div>}
  </main>;
}

createRoot(document.getElementById('root')!).render(<App/>);
