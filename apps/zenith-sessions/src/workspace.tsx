import { invoke } from '@tauri-apps/api/core';
import { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AccountInspector, Avatar, type Account } from './account-inspector';
import { ClientPerformance } from './client-performance';
import { LegacyProfileDetails, LegacyWorkspacePanel, type LegacyWorkspace } from './legacy-workspace';
import { RecoveryPanel, type Recovery } from './recovery-panel';
import { DestinationPicker, type Destination } from './destination-picker';
import { Icon, type IconName } from './design-icons';
import './zenith-design.css';
import './workspace.css';

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
type LaunchState = { active: number; pairEnabled: boolean; hold: string | null };
type PanicState = { epoch: string; held: boolean; canResume: boolean; shortcutAvailable: boolean; error: string | null };
type Snapshot = {
  accounts: Account[]; sessions: Session[]; launch: LaunchState; admission: Admission | null;
  recovery: Recovery;
  panic: PanicState;
  signInAttempt: string | null; signInError: string | null;
  savedSignIn: { attempt: string; profileId: string } | null;
};
type Page = 'Overview' | 'Accounts' | 'Sessions' | 'Windows' | 'Performance';
type WindowPlacement = {
  attemptId: string;
  placement: 'placed' | 'changed' | 'rejected' | 'unconfirmed' | 'cancelled';
};
type MigrationReport = { profiles: number; added: number; signInsAdded: number; existingKept: number };

const pages: [Page, IconName][] = [['Overview', 'grid'], ['Accounts', 'users'], ['Sessions', 'play'], ['Windows', 'window'], ['Performance', 'bolt']];
const pageDescription: Record<Page, string> = {
  Overview: 'Review your local workspace and launch capacity.',
  Accounts: 'Manage your saved Roblox sign-ins.',
  Sessions: 'Choose a saved account and a Roblox place.',
  Windows: 'Focus or arrange your confirmed Roblox client windows.',
  Performance: 'Measure the clients Zenith owns and adjust their Windows priority.',
};
const placementLabel: Record<WindowPlacement['placement'], string> = {
  placed: 'Position confirmed',
  changed: 'The window or monitor changed',
  rejected: 'Windows rejected the move',
  unconfirmed: 'The requested bounds could not be confirmed',
  cancelled: 'Move cancelled',
};
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
  const [panicBusy, setPanicBusy] = useState(false);
  const [pairRequested, setPairRequested] = useState(false);
  const [importing, setImporting] = useState(false);
  const [legacyWorkspace, setLegacyWorkspace] = useState<LegacyWorkspace | null>(null);
  const [legacyOffset, setLegacyOffset] = useState(0);
  const [legacyRevision, setLegacyRevision] = useState(0);
  const [legacyNotice, setLegacyNotice] = useState('');
  const [signInBusy, setSignInBusy] = useState(false);
  const [arranging, setArranging] = useState(false);
  const [placements, setPlacements] = useState<WindowPlacement[]>([]);
  const [avatars, setAvatars] = useState<Record<string, string>>({});
  const [avatarRevision, setAvatarRevision] = useState(0);
  const [headshotNotice, setHeadshotNotice] = useState('');
  const [remove, setRemove] = useState<Account | null>(null);
  const file = useRef<HTMLInputElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const reading = useRef(false);
  const seenSignIn = useRef('');
  const accounts = snapshot?.accounts ?? [];
  const accountKey = accounts.map(item => item.profileId).join(',');
  const importedTheme = themes.find(([id]) => id === legacyWorkspace?.theme)?.[0];
  const inspected = accounts.find(value => value.profileId === selected);
  const account = accounts.find(value => value.profileId === selected) ?? (accounts.length === 1 ? accounts[0] : undefined);
  const sessions = snapshot?.sessions ?? [];
  const confirmedWindows = sessions.filter(session => session.phase === 'running');
  const windowRows = sessions.filter(session => isBusy(session) || placements.some(result => result.attemptId === session.attemptId));
  const launchState = snapshot?.launch;
  const panic = snapshot?.panic;
  const recovery = snapshot?.recovery;
  const hasRecovery = recovery && (recovery.issue || recovery.attempts.length > 0);
  const active = launchState?.active ?? 0;
  const pairEnabled = active > 0 ? launchState?.pairEnabled === true : pairRequested;
  const selectedSession = sessions.find(session => session.accountId === account?.profileId);
  const admission = snapshot?.admission;
  const signInAttempt = snapshot?.signInAttempt;
  const savedSignIn = snapshot?.savedSignIn;
  const filtered = accounts.filter(item => item.username.toLowerCase().includes(query.trim().toLowerCase()));
  const placeEntered = Boolean(destination.place.trim());
  const cannotLaunch = !account || !placeEntered || isBusy(selectedSession) || launching || panicBusy || !panic || panic.held || !launchState || !!launchState.hold;

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
  useEffect(() => {
    let current = true;
    void invoke<LegacyWorkspace | null>('legacy_workspace', { offset: legacyOffset }).then(
      workspace => { if (current) setLegacyWorkspace(workspace); },
      error => { if (current) setLegacyNotice(typeof error === 'string' ? error : 'Could not read the legacy workspace.'); },
    );
    return () => { current = false; };
  }, [accountKey, legacyOffset, legacyRevision]);
  useEffect(() => {
    if (!accountKey) {
      setAvatars({});
      setHeadshotNotice('');
      return;
    }
    let current = true;
    let retry: number | undefined;
    const ids = accountKey.split(',');
    const received = new Set<string>();
    const delays = [1500, 4500];
    setHeadshotNotice('');
    setAvatars(previous => Object.fromEntries(ids.filter(id => previous[id]).map(id => [id, previous[id]])));
    async function loadHeadshots(attempt: number) {
      try {
        const images = await invoke<Record<string, string>>('account_headshots');
        if (!current) return;
        const completed = ids.filter(id => images[id]);
        completed.forEach(id => received.add(id));
        setAvatars(previous => ({ ...previous, ...Object.fromEntries(completed.map(id => [id, images[id]])) }));
        if (received.size === ids.length) return;
      } catch {
        if (!current) return;
      }
      const delay = delays[attempt];
      if (delay === undefined) {
        setHeadshotNotice('Some headshots could not be refreshed. Use Refresh to try again.');
      } else {
        retry = window.setTimeout(() => void loadHeadshots(attempt + 1), delay);
      }
    }
    void loadHeadshots(0);
    return () => { current = false; window.clearTimeout(retry); };
  }, [accountKey, avatarRevision, savedSignIn?.attempt]);
  useEffect(() => {
    if (savedSignIn && seenSignIn.current !== savedSignIn.attempt) {
      seenSignIn.current = savedSignIn.attempt;
      setSelected(savedSignIn.profileId);
      setQuery('');
    }
  }, [savedSignIn]);

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

  async function changePanic(epoch?: string) {
    setPanicBusy(true);
    try { await (epoch ? act('resume_launches', { epoch }) : act('panic_stop', {})); }
    finally { setPanicBusy(false); }
  }

  async function importFile(input: HTMLInputElement) {
    const selectedFile = input.files?.[0];
    input.value = '';
    if (!selectedFile) return;
    const workspace = selectedFile.name.toLowerCase().endsWith('.zenith-migration');
    if (selectedFile.size === 0 || selectedFile.size > (workspace ? 16 * 1024 * 1024 : 131072)) {
      setFailure(workspace ? 'Choose a protected legacy workspace of up to 16 MiB.' : 'Choose a protected Zenith sign-in bundle of up to 128 KiB.');
      return;
    }
    setImporting(true);
    setFailure('');
    try {
      const bytes = await selectedFile.arrayBuffer();
      if (workspace) {
        const result = await invoke<MigrationReport>('import_legacy', bytes);
        setLegacyNotice(`Preserved ${result.profiles} profiles. Added ${result.added} accounts and ${result.signInsAdded} sign-ins. Kept ${result.existingKept} matching accounts.`);
        setLegacyOffset(0);
        setLegacyRevision(value => value + 1);
      } else {
        const imported = await invoke<Account>('import_account', { encrypted: Array.from(new Uint8Array(bytes)) });
        setSelected(imported.profileId);
      }
    } catch (error) {
      setFailure(typeof error === 'string' ? error : 'Could not read the protected sign-in bundle.');
    } finally {
      setImporting(false);
      void refresh();
    }
  }

  async function launch(tryOne: boolean) {
    if (cannotLaunch || !account || !panic || (tryOne && active > 0)) return;
    setLaunching(true);
    try {
      if (await act('launch_account', {
        id: account.profileId, placeId: destination.place, job: destination.job, tryOne, allowPair: pairEnabled,
        epoch: panic.epoch,
      })) {
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

  async function tileWindows() {
    if (!confirmedWindows.length || arranging) return;
    setArranging(true);
    setPlacements([]);
    setFailure('');
    try {
      setPlacements(await invoke<WindowPlacement[]>('tile_windows', {
        attempts: confirmedWindows.map(session => session.attemptId),
      }));
    } catch (error) {
      setFailure(typeof error === 'string' ? error : 'Could not arrange the confirmed windows.');
    } finally {
      setArranging(false);
      void refresh();
    }
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
        <button className="panic-button" title={panic?.shortcutAvailable ? 'Ctrl+Shift+P' : undefined}
          aria-keyshortcuts={panic?.shortcutAvailable ? 'Control+Shift+P' : undefined}
          disabled={panicBusy} onClick={() => void changePanic()}>Panic stop</button>
        <button className="icon-button" aria-label="Refresh" onClick={() => { setAvatarRevision(value => value + 1); void refresh(); }}><Icon name="refresh" size={16}/></button>
        <button className="icon-button" aria-label="Appearance" aria-expanded={showAppearance} onClick={() => setShowAppearance(value => !value)}><Icon name="sun" size={16}/></button>
      </div>
    </header>
    <section className="workspace">
      {panic && !panic.shortcutAvailable && <p className="panic-shortcut-note" role="status">The global Panic shortcut is unavailable. Use the Panic stop button.</p>}
      {panic?.held && <section className="inline-alert panic-hold" aria-label="Panic stop status">
        <div role="status"><strong>New launches are held.</strong>
          {panic.error ? <p>{panic.error} Select Panic stop to retry.</p>
            : !panic.canResume && <p>Waiting for owned clients and launch work to finish.</p>}
        </div>
        {panic.canResume && <button className="secondary-button" disabled={panicBusy} onClick={() => void changePanic(panic.epoch)}>Resume launches</button>}
      </section>}
      {showAppearance && <section className="account-panel preview-panel appearance-preview">
        <label>Appearance <select value={theme} onChange={event => {
          const choice = themes.find(([id]) => id === event.target.value);
          if (choice) setTheme(choice[0]);
        }}>{themes.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      </section>}
      <div className="page-heading">
        <div><h1>{page}</h1><p>{pageDescription[page]}</p></div>
        {page === 'Accounts' && <div className="heading-actions">
          <button className="secondary-button" disabled={importing} onClick={() => file.current?.click()}>{importing ? 'Importing…' : 'Import protected file'}</button>
          <button className="primary-button" disabled={signInBusy || !!signInAttempt} onClick={() => void beginSignIn()}><Icon name="plus" size={16}/> Add account</button>
        </div>}
      </div>
      <input ref={file} type="file" accept=".dpapi,.zenith-migration" hidden onChange={event => void importFile(event.currentTarget)}/>
      {loadingError && <p role="alert" className="inline-alert">Zenith could not read the local workspace. Select Refresh to retry.</p>}
      {failure && <p role="alert" className="inline-alert">{failure}</p>}
      {hasRecovery && page !== 'Sessions' && <div className="inline-alert row-actions">
        <span>Launch recovery has entries to review.</span>
        <button className="secondary-button" onClick={() => setPage('Sessions')}>Open Sessions</button>
      </div>}
      {page === 'Accounts' && legacyNotice && <p role="status" className="inline-alert">{legacyNotice}</p>}
      {signInAttempt && <section className="account-panel preview-panel" aria-label="Roblox sign-in">
        <h2>Roblox sign-in</h2>
        <p>Finish signing in inside the Roblox window. Zenith will save the verified sign-in and close the window automatically.</p>
        {snapshot?.signInError && <p role="status">{snapshot.signInError}</p>}
        <div className="row-actions">
          {snapshot?.signInError && <button className="primary-button" disabled={signInBusy} onClick={() => void saveSignIn()}>{signInBusy ? 'Checking sign-in…' : 'Retry save'}</button>}
          <button className="secondary-button" onClick={() => void act('cancel_sign_in', { attempt: signInAttempt })}>Cancel sign-in</button>
        </div>
      </section>}
      {page === 'Overview' && <>
        <div className="metrics-grid preview-metrics">
          <article className="metric-card"><div className="metric-top">Saved accounts</div><strong>{accounts.length}</strong></article>
          <article className="metric-card"><div className="metric-top">Active sessions</div><strong>{active}</strong></article>
          <article className="metric-card"><div className="metric-top">Available memory</div><strong>{admission ? gib(admission.physicalAvailable) : 'Unavailable'}</strong></article>
        </div>
        <Memory admission={admission}/>
      </>}
      {page === 'Accounts' && <div className={`accounts-workspace${inspected ? ' with-inspector' : ''}`}><section className="account-panel">
        <div className="panel-heading"><div><h2>Account library</h2><p>{filtered.length} of {accounts.length} accounts shown</p></div></div>
        <div className="toolbar"><label className="search-box"><Icon name="search" size={17}/><input ref={search} value={query} onChange={event => setQuery(event.target.value)} placeholder="Search accounts…" aria-label="Search accounts"/><kbd>Ctrl K</kbd></label></div>
        {accounts.length > 0 && headshotNotice && <p className="headshot-notice" role="status">{headshotNotice}</p>}
        {filtered.length ? <div className="table-wrap"><table>
          <thead><tr><th>Account</th><th>Status</th><th>Actions</th></tr></thead>
          <tbody>{filtered.map(item => {
            const session = sessions.find(session => session.accountId === item.profileId);
            return <tr key={item.profileId} className={item.profileId === inspected?.profileId ? 'selected-row' : ''}>
            <td><button className="account-cell account-select" type="button" aria-pressed={item.profileId === inspected?.profileId}
              aria-label={`View details for ${item.username}`} onClick={() => setSelected(item.profileId)}>
              <Avatar key={`${avatarRevision}:${avatars[item.profileId] ?? item.profileId}`} username={item.username} image={avatars[item.profileId]}/>
              <div><strong>{item.username}</strong><span>@{item.username}</span></div>
            </button></td>
            <td><Status session={session}/></td>
            <td><div className="row-actions"><button className="launch-button" onClick={() => openLaunch(item)}><Icon name="play" size={14}/> Select for launch</button>
              <button className="secondary-button" disabled={isBusy(session)} onClick={() => setRemove(item)}>Remove</button></div></td>
          </tr>; })}</tbody>
        </table></div> : <div className="empty-state"><strong>{accounts.length ? 'No matching accounts' : 'No saved sign-ins'}</strong>
          <span>{accounts.length ? 'Try a different search.' : 'Add an account through Roblox or import a protected bundle from Zenith.'}</span></div>}
      </section>
      {inspected && <AccountInspector key={`${inspected.profileId}:${savedSignIn?.profileId === inspected.profileId ? savedSignIn.attempt : ''}`}
        account={inspected} image={avatars[inspected.profileId]} imageRevision={avatarRevision}
        legacyDetails={legacyWorkspace?.linked[inspected.profileId] && <LegacyProfileDetails profile={legacyWorkspace.linked[inspected.profileId]}/>}
        onFavorite={favorite => {
          setDestination({ place: favorite.placeId, name: favorite.name, job: null });
          openLaunch(inspected);
        }}/>}
      </div>}
      {page === 'Accounts' && legacyWorkspace && <LegacyWorkspacePanel workspace={legacyWorkspace} offset={legacyOffset}
        onPage={setLegacyOffset} onFavorite={favorite => {
          setDestination({ place: favorite.placeId, name: favorite.name, job: favorite.jobId });
          setPage('Sessions');
        }} onTheme={importedTheme ? () => setTheme(importedTheme) : undefined}/>}
      {page === 'Sessions' && <>
        {hasRecovery && <RecoveryPanel recovery={recovery}
          onDismiss={attempt => void act('dismiss_recovered_launch', { attempt })}
          onArchive={() => void act('archive_launch_journal', {})}/>}
        <section className="account-panel preview-panel">
          <h2>Launch Roblox</h2>
          <div className="session-fields">
            <label>Account <select value={account?.profileId ?? ''} disabled={launching} onChange={event => setSelected(event.target.value)}>
              <option value="" disabled>Choose an account</option>{accounts.map(item => <option key={item.profileId} value={item.profileId}>{item.username}</option>)}
            </select></label>
          </div>
          <DestinationPicker destination={destination} onChange={setDestination} disabled={launching}/>
          <label className="pair-choice">
            <input type="checkbox" checked={pairEnabled} disabled={active > 0 || launching} onChange={event => setPairRequested(event.target.checked)}/>
            Enable two-account trial
          </label>
          {pairEnabled && <p>Start with other Roblox clients closed. Launch one account, then choose a different account when the first is confirmed and ready.</p>}
          {launchState?.hold && <p role="status">{launchState.hold}</p>}
          {isBusy(selectedSession) && <p>This account already has an active session.</p>}
          <div className="row-actions"><button className="primary-button" disabled={cannotLaunch || !admission || !!admission.limitingResource} onClick={() => void launch(false)}><Icon name="play" size={15}/> Launch</button>
            {admission?.canTryOne && active === 0 && <button className="secondary-button" disabled={cannotLaunch} onClick={() => void launch(true)}>Try one account anyway</button>}
          </div>
        </section>
        {active > 0 && <section className="account-panel preview-panel row-actions" aria-label="Session controls">
          <button className="secondary-button" onClick={() => void act('stop_all_sessions', {})}>Stop all owned clients</button>
        </section>}
        {sessions.map(session => <section key={session.attemptId} className="account-panel preview-panel session-status" aria-label={`${session.username} session`}>
          <div className="session-title"><h2>{session.username}</h2><Status session={session}/></div>
          <p>Place {session.placeId}{session.pid ? ` · PID ${session.pid}` : ''}</p>
          {session.requestedServer && <p>Requested server {session.requestedServer}</p>}
          {session.detail && <p>{session.detail}</p>}
          {session.observedServer && <p>Confirmed initial server {session.observedServer}</p>}
          {isBusy(session) && <div className="row-actions">
            {session.phase === 'running' && <button className="secondary-button" onClick={() => void act('focus_session', { attempt: session.attemptId })}>Focus Roblox</button>}
            <button className="secondary-button" disabled={session.phase === 'stopping'} onClick={() => void act('stop_session', { attempt: session.attemptId })}>{session.phase === 'preparing' ? 'Cancel launch' : 'Stop Roblox'}</button>
          </div>}
        </section>)}
        <Memory admission={admission}/>
      </>}
      {page === 'Windows' && <>
        <section className="account-panel preview-panel">
          <h2>Desktop layout</h2>
          <p>Tile confirmed windows on the monitor containing Zenith. Use restored, resizable Roblox windows.</p>
          <button className="primary-button" disabled={arranging || panic?.held || !confirmedWindows.length} onClick={() => void tileWindows()}>
            <Icon name="grid" size={16}/>{arranging ? 'Arranging…' : 'Tile confirmed windows'}
          </button>
          {!confirmedWindows.length && <p>No Roblox windows are confirmed yet.</p>}
        </section>
        {windowRows.length > 0 && <section className="window-cards" aria-label="Roblox windows">
          {windowRows.map(session => {
            const result = placements.find(value => value.attemptId === session.attemptId);
            return <article key={session.attemptId} className="account-panel preview-panel session-status">
              <div className="session-title"><h2>{session.username}</h2><Status session={session}/></div>
              {session.pid && <p>PID {session.pid} · Place {session.placeId}</p>}
              {session.phase === 'running' && <button className="secondary-button" disabled={arranging}
                onClick={() => void act('focus_session', { attempt: session.attemptId })}>Focus Roblox</button>}
              {result && <p role="status">{placementLabel[result.placement]}</p>}
            </article>;
          })}
        </section>}
      </>}
      {page === 'Performance' && <>
        <ClientPerformance/>
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
