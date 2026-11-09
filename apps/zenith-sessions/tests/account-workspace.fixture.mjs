import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtemp, readFile, realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, extname, join, resolve } from 'node:path';
import { promisify } from 'node:util';

function exerciseWorkspace() {
  const accounts = Array.from({ length: 6 }, (_, index) => ({
    profileId: `fixture-${index + 1}`, username: `Fixture${index + 1}`,
  }));
  let savedSignIn = null;
  let signInAttempt = null;
  let signInClosing = null;
  let manualSaveCalls = 0;
  let panic = { epoch: '0', held: false, canResume: false, shortcutAvailable: true, error: null };
  let launchedEpoch;
  let launchedTarget;
  let sessionRows = [];
  let stoppedRecovery;
  let reportAttempts = 0;
  let efficiencyChanges = 0;
  const windowChanges = [];
  let efficiency = { policy: 'system_managed', canRequest: true, canRestore: false };
  let savedInvites = [];
  const inviteLink = 'https://www.roblox.com/games/1819/Fixture?privateServerLinkCode=12345678901234567890123456789012';
  let catalogRequests = 0;
  const catalogCursor = { offset: 25, hostSessionId: 'fixture-session', catalogRevision: '9007199254740993', total: 26 };
  const selectionPage = { ...catalogCursor, offset: 0 };
  const emptyCatalog = { state: 'Disconnected', items: [], total: 0, offset: 0, hostVersion: '', previous: null, next: null };
  const assignmentTarget = { kind: 'Account', id: 'fixture-6' };
  let assignment = null;
  let assignmentStamp = 'initial-stamp';
  let reviewCalls = 0;
  let readyWindow = false;
  let macroPlayback = null;
  const pairingCode = '00112233445566778899aabbccddeeff';
  let releaseReview;
  const lateReview = new Promise(resolve => { releaseReview = resolve; });
  const assignmentView = () => ({ stamp: assignmentStamp, total: 1, offset: 0, previous: null, next: null,
    unreadableLegacy: 0, rows: [{ target: assignmentTarget, name: 'Fixture6', editable: true, inherited: false, assignment }] });
  localStorage.setItem('zenith-preview-place', '1818');
  let headshotRequests = 0;
  let releaseGame;
  let detailsRequested = false;
  let detailsResolved = false;
  let pendingPrivateProfile = false;
  let repeatedPrivateProfile = false;
  let releasePrivateProfile;
  let releaseFirstProfile;
  const firstProfile = new Promise(resolve => { releaseFirstProfile = resolve; });
  window.__TAURI_INTERNALS__ = {
    invoke: async (command, args = {}) => {
      const { id, epoch, cursor, stamp, target, edit, choice, action, request, place, universe, placeId, job } = args;
      switch (command) {
        case 'client_performance': return [{
          attemptId: 'owned-efficiency', username: 'Fixture6', pid: 1777,
          metrics: { cpuPercent: 4, readBytesPerSecond: null, writeBytesPerSecond: null, workingSet: null,
            privateWorkingSet: null, privateCommit: null, uptimeSeconds: 30, priority: null, efficiency },
        }];
        case 'set_client_efficiency':
          if (args.attempt !== 'owned-efficiency') throw new Error('The efficiency action targeted another attempt.');
          if (efficiencyChanges++ === 0 && args.action === 'request') {
            efficiency = { policy: 'efficiency_requested', canRequest: false, canRestore: true };
          } else if (efficiencyChanges === 2 && args.action === 'restore') {
            efficiency = { policy: 'system_managed', canRequest: true, canRestore: false };
          } else throw new Error('Unexpected efficiency action.');
          return efficiency;
        case 'set_window_visibility':
          if (args.attempt !== 'visible-window') throw new Error('The window action targeted another attempt.');
          windowChanges.push(args.action);
          if (windowChanges.length === 1) throw 'Windows did not confirm the window change.';
          return;
        case 'export_support_report':
          if (Object.keys(args).length !== 0) throw new Error('The report export accepted a renderer-supplied path or data.');
          if (++reportAttempts === 1) throw 'Could not save the support report.';
          return { path: 'C:\\Fixture\\support-reports\\zenith-report.json' };
        case 'workspace_snapshot': return {
          accounts, sessions: sessionRows, launch: { active: sessionRows.length, pairEnabled: false, hold: null },
          admission: { limitingResource: null, physicalAvailable: 1, physicalRequired: 1,
            commitAvailable: 1, commitRequired: 1, canTryOne: false },
          recovery: { issue: null, attempts: [] }, panic,
          signInAttempt, signInClosing, signInError: null, savedSignIn, macroPlayback,
        };
        case 'begin_sign_in':
          signInAttempt = 'fixture-sign-in';
          return signInAttempt;
        case 'finish_sign_in':
          manualSaveCalls++;
          throw new Error('Automatic saving must not require a renderer save command.');
        case 'legacy_workspace': return null;
        case 'macro_assignments': return assignmentView();
        case 'edit_macro_assignment':
          if (stamp !== assignmentStamp || target.id !== assignmentTarget.id || target.kind !== 'Account') throw new Error('Wrong assignment target or stamp.');
          if (edit.action === 'Save') {
            if (JSON.stringify(edit.choice.page) !== JSON.stringify(selectionPage) || edit.choice.macroId !== 'macro-0' || edit.choice.savedRevision !== 'fixture') throw new Error('The selected macro was not pinned.');
            assignment = { id: 'macro-0', name: 'Fixture macro 1', savedRevision: 'fixture' };
          } else if (edit.action === 'Remove') assignment = null;
          else throw new Error('The edit must be explicit.');
          assignmentStamp += 'x';
          return assignmentView();
        case 'review_macro_targets':
          if (epoch !== panic.epoch || panic.held || stamp !== assignmentStamp
            || target.id !== assignmentTarget.id || target.kind !== 'Account'
            || JSON.stringify(choice.page) !== JSON.stringify(selectionPage)
            || choice.macroId !== 'macro-0' || choice.savedRevision !== 'fixture') {
            throw new Error('The review did not bind the exact assignment, macro revision and Panic epoch.');
          }
          reviewCalls++;
          if (reviewCalls === 3) await lateReview;
          return { targetName: 'Fixture6', macroName: 'Fixture macro 1', playbackReview: readyWindow ? 'fixture-review' : null,
            recipients: [{ name: 'Fixture6', session: readyWindow
              ? { status: 'ready', attemptId: 'fixture-attempt', pid: 4321 } : { status: 'noSession' } }] };
        case 'start_macro_playback':
          if (request.epoch !== panic.epoch || panic.held || request.stamp !== assignmentStamp || request.target.id !== assignmentTarget.id
            || request.attempt !== 'fixture-attempt' || request.reviewId !== 'fixture-review' || request.code !== pairingCode || request.choice.savedRevision !== 'fixture') {
            throw new Error('Start did not bind the reviewed assignment and attempt.');
          }
          macroPlayback = { id: 'fixture-operation', name: 'Fixture macro 1', account: 'Fixture6', phase: 'running', detail: 'Foreground playback is running.' };
          return macroPlayback.id;
        case 'control_macro_playback':
          if (id !== macroPlayback?.id) throw new Error('Control did not use the current operation ID.');
          macroPlayback = { ...macroPlayback, phase: { pause: 'paused', resume: 'running', stop: 'finished' }[action],
            detail: { pause: 'Paused.', resume: 'Foreground playback is running.', stop: 'Macro stopped and cleanup finished.' }[action] };
          return;
        case 'saved_macro_catalog':
          catalogRequests++;
          if (catalogRequests === 1) return emptyCatalog;
          if (catalogRequests === 2 || catalogRequests === 4) return { ...emptyCatalog, state: 'Ready', total: 26, hostVersion: 'Fixture 2', selectionPage,
            next: catalogCursor, items: Array.from({ length: 25 }, (_, index) => ({ id: `macro-${index}`,
              savedRevision: 'fixture', name: `Fixture macro ${index + 1}`, hotkey: '',
              topLevelActions: 3, enabled: true, targetMode: 'Screen' })) };
          if (JSON.stringify(cursor) !== JSON.stringify(catalogCursor)) throw new Error('Catalog cursor was not preserved.');
          return { ...emptyCatalog, state: 'Changed' };
        case 'panic_stop':
          panic = { ...panic, epoch: String((BigInt(panic.epoch) | 1n) + 2n), held: true };
          return;
        case 'resume_launches':
          if (epoch !== '3' || !panic.canResume) throw new Error('The fixture requires confirmed cleanup.');
          panic = { ...panic, epoch: '4', held: false, canResume: false };
          return;
        case 'launch_account':
          if (panic.held || epoch !== panic.epoch) throw new Error('The fixture rejected a stale launch permit.');
          launchedEpoch = epoch;
          launchedTarget = args;
          return;
        case 'stop_session':
          stoppedRecovery = args.attempt;
          sessionRows = [];
          return;
        case 'private_servers': return savedInvites;
        case 'save_private_server':
          if (args.invite !== inviteLink || args.name !== 'Fixture private server') throw new Error('Unexpected private save payload.');
          savedInvites = [{ id: 'invite-1', revision: 'r1', name: args.name, placeId: '1819' }];
          return savedInvites[0];
        case 'rename_private_server':
          if (args.reference?.id !== 'invite-1' || args.reference.revision !== 'r1' || args.name !== 'Renamed invite') {
            throw new Error('Rename used the wrong private reference.');
          }
          savedInvites = [{ ...savedInvites[0], name: args.name, revision: 'r2' }];
          return savedInvites[0];
        case 'remove_private_server':
          if (args.reference?.id !== 'invite-1' || args.reference.revision !== 'r2') throw new Error('Remove used a stale private reference.');
          savedInvites = [];
          return;
        case 'launch_private_server':
          if (panic.held || epoch !== panic.epoch) throw new Error('The fixture rejected a stale private launch permit.');
          launchedTarget = args;
          return;
        case 'browse_public_servers':
          return { servers: [{ id: '11111111-0000-0000-0000-000000000001', playing: 4, maxPlayers: 8, ping: null }], nextCursor: null };
        case 'public_game_details': {
          if (place === '1818') {
            detailsRequested = true;
            await new Promise(resolve => { releaseGame = resolve; });
            detailsResolved = true;
          }
          return { requestedPlaceId: place, rootPlaceId: '4000', universeId: place === '1818' ? '5000' : '5001',
            name: place === '1818' ? 'Superseded game' : 'Fixture experience', creator: 'Fixture studio',
            playing: '1234', visits: '9007199254740999', maxPlayers: 10, description: 'Fixture description.' };
        }
        case 'public_game_art':
          if (universe !== '5001') throw new Error('Artwork was requested for a stale or wrong universe.');
          return { icon: { Ok: '/headshot.svg?icon' }, thumbnails: { Ok: ['/headshot.svg?thumbnail'] } };
        case 'account_headshots':
          headshotRequests++;
          return Object.fromEntries(accounts.slice(0, headshotRequests === 1 ? 5 : 6)
            .map(account => [account.profileId, `/headshot.svg?${account.profileId}`]));
        case 'account_profile': {
          if (id === 'fixture-1') await firstProfile;
          return { userId: id, displayName: `${id} display name`, created: '2020-01-01', description: '' };
        }
        case 'account_favorites': return { items: [{ placeId: '12345', name: `${id} favorite` }], nextCursor: null };
        case 'account_private_profile': {
          if (id === 'fixture-3') {
            if (pendingPrivateProfile) {
              repeatedPrivateProfile = true;
              throw 'Another operation is checking this account. Retry shortly.';
            }
            if (!releasePrivateProfile) {
              pendingPrivateProfile = true;
              await new Promise(resolve => { releasePrivateProfile = resolve; });
              pendingPrivateProfile = false;
            }
          }
          return {
            balance: { Ok: savedSignIn?.profileId === id ? '73' : '0' },
            friends: { Ok: { items: [{ id: '42', name: '', displayName: `${id} friend` }], nextCursor: null } },
          };
        }
        default: throw new Error(`Unexpected fixture command: ${command}`);
      }
    },
  };

  const find = (selector) => document.querySelector(selector);
  const row = (index) => find(`button[aria-label="View details for Fixture${index}"]`);
  const details = () => find('[aria-label="Account details"]')?.textContent ?? '';
  async function until(predicate, message) {
    const deadline = Date.now() + 4000;
    do {
      if (predicate()) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    } while (Date.now() < deadline);
    throw new Error(message);
  }

  async function run() {
    const checks = [];
    async function check(label, test) {
      try { await test(); checks.push(`PASS ${label}`); }
      catch (error) { checks.push(`FAIL ${label} (${error.message})`); }
    }
    await check('the first saved account is already selected in Accounts', async () => {
      await until(() => row(1)?.getAttribute('aria-pressed') === 'true' && details().includes('fixture-1 favorite'),
        'opening Accounts left the inspector empty');
    });
    await check('the full account cell is a readable selection target', async () => {
      const button = row(1);
      const bounds = button.getBoundingClientRect();
      const cell = button.closest('td').getBoundingClientRect();
      if (bounds.height < 44 || cell.right - bounds.right > 24) {
        throw new Error('only the small account label is clickable');
      }
    });
    await check('pending headshot is retried and rendered', async () => {
      await until(() => row(6)?.querySelector('img:not([hidden])')?.naturalWidth > 0,
        'sixth headshot stayed on initials');
      if (headshotRequests !== 2) throw new Error(`expected 2 requests, got ${headshotRequests}`);
    });
    await check('a completed headshot whose image fails to load is retried', async () => {
      await until(() => row(2)?.querySelector('img:not([hidden])')?.naturalWidth > 0,
        'a failed CDN image stayed on initials');
    });
    await check('switching accounts replaces all selected details', async () => {
      row(1).click();
      await until(() => details().includes('fixture-1 favorite'), 'first account did not load');
      row(6).click();
      await until(() => details().includes('fixture-6 display name') && details().includes('fixture-6 favorite')
        && details().includes('fixture-6 friend') && /Robux0/.test(details()), 'sixth account details did not load');
      releaseFirstProfile();
      await new Promise(resolve => setTimeout(resolve, 100));
      if (details().includes('fixture-1')) throw new Error('previous account results leaked into current details');
    });
    await check('renewal refreshes the already selected account', async () => {
      savedSignIn = { profileId: 'fixture-6', attempt: 'renewed-fixture', closeFailed: false };
      await until(() => /Robux73/.test(details()), 'renewed account still showed the old balance');
      if (!document.querySelector('.sign-in-saved')?.textContent.includes('@Fixture6')) {
        throw new Error('a successful save did not identify the account that was saved');
      }
      savedSignIn = { ...savedSignIn, closeFailed: true };
      signInClosing = savedSignIn.attempt;
      await until(() => find('.sign-in-saved')?.textContent.includes('You can close it manually'),
        'a saved sign-in with a close failure did not show the separate close warning');
      signInClosing = null;
    });
    await check('returning to an account reuses its unfinished private lookup', async () => {
      row(3).click();
      await until(() => pendingPrivateProfile, 'the original lookup did not start');
      try {
        row(4).click();
        await until(() => details().includes('fixture-4 friend'), 'the other account did not load');
        row(3).click();
        await until(() => details().includes('fixture-3 favorite'), 'the account did not reopen');
        await new Promise(resolve => setTimeout(resolve, 200));
        if (repeatedPrivateProfile) throw new Error('the return lookup collided with the original lookup');
      } finally { releasePrivateProfile(); }
      await until(() => details().includes('fixture-3 friend'), 'the returning inspector did not receive the original result');
    });
    await check('all six saved accounts show their own headshot and details', async () => {
      for (let index = 1; index <= accounts.length; index++) {
        row(index).click();
        const id = `fixture-${index}`;
        await until(() => row(index).getAttribute('aria-pressed') === 'true'
          && details().includes(`${id} display name`) && details().includes(`${id} favorite`)
          && details().includes(`${id} friend`) && details().includes(index === 6 ? 'Robux73' : 'Robux0')
          && new URL(find('[aria-label="Account details"] img:not([hidden])')?.src ?? location.href).search.startsWith(`?${id}`),
        `account ${index} showed missing or mismatched details`);
      }
    });
    await check('automatic save selects the saved account and waits for the sign-in window to close', async () => {
      const add = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === 'Add account');
      await until(() => add && !add.disabled, 'the previous sign-in did not finish closing');
      row(4).click();
      add.click();
      await until(() => find('[aria-label="Roblox sign-in"]') && add.disabled, 'the sign-in flow did not start');
      signInClosing = signInAttempt;
      signInAttempt = null;
      savedSignIn = { profileId: 'fixture-6', attempt: 'fixture-sign-in', closeFailed: false };
      await until(() => !find('[aria-label="Roblox sign-in"]') && find('.sign-in-saved')?.textContent.includes('Closing'),
        'the UI did not show that the sign-in was saved and the window was closing');
      if (!add.disabled || manualSaveCalls || row(6).getAttribute('aria-pressed') !== 'true') {
        throw new Error('automatic save did not lock the next sign-in or select the saved profile');
      }
      signInClosing = null;
      await until(() => !add.disabled && !find('.sign-in-saved')?.textContent.includes('Closing'),
        'the next account could not sign in after window closure');
    });
    await check('game details and artwork keep the exact requested destination', async () => {
      const button = label => [...document.querySelectorAll('button')].find(element => element.textContent.trim() === label);
      find('button[aria-label="Sessions"]').click();
      try {
        await until(() => button('Load game details'), 'the Sessions picker does not offer public game details');
        button('Load game details').click();
        await until(() => detailsRequested, 'the initial place was not requested');
        const destination = find('input[placeholder="Enter a place ID or game link"]');
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(destination, '1819');
        destination.dispatchEvent(new Event('input', { bubbles: true }));
        await until(() => button('Load game details') && !button('Load game details').disabled, 'changing the place did not reset details');
        button('Browse public servers').click();
        await until(() => find('[aria-label="Public servers"] button'), 'the server list did not load');
        find('[aria-label="Public servers"] button').click();
        button('Load game details').click();
        const panel = () => find('[aria-label="Public game details"]');
        await until(() => panel()?.textContent.includes('Fixture experience') && panel()?.textContent.includes('Fixture studio')
          && panel()?.querySelectorAll('img').length === 2 && [...panel().querySelectorAll('img')].every(image => image.naturalWidth > 0),
          'the selected game metadata, icon or thumbnail did not load');
        releaseGame();
        await until(() => detailsResolved, 'the stale fixture request did not finish');
        await new Promise(resolve => setTimeout(resolve, 100));
        if (panel().textContent.includes('Superseded game') || !panel().textContent.includes('1819') || !panel().textContent.includes('4000')
          || destination.value !== '1819' || !find('[aria-label="Public servers"] button[aria-pressed="true"]')) {
          throw new Error('a stale lookup or the experience root replaced the exact launch destination');
        }
        button('Launch').click();
        await until(() => launchedTarget?.placeId === '1819' && launchedTarget?.job === '11111111-0000-0000-0000-000000000001',
          'Launch did not receive the exact requested subplace and pinned server');
      } finally {
        releaseGame?.();
        find('button[aria-label="Accounts"]').click();
      }
    });
    await check('a protected invite launches by reference without a public target or raw code', async () => {
      const button = label => [...document.querySelectorAll('button')].find(element => element.textContent.trim() === label);
      const type = (element, value) => {
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(element, value);
        element.dispatchEvent(new Event('input', { bubbles: true }));
      };
      find('button[aria-label="Sessions"]').click();
      try {
        await until(() => find('[aria-label="Private servers"]'), 'no private server workflow in Sessions');
        const panel = find('[aria-label="Private servers"]');
        panel.querySelector('summary').click();
        type(find('input[aria-label="Private server name"]'), 'Fixture private server');
        type(find('input[aria-label="Private Roblox invite link"]'), inviteLink);
        button('Save private invite').click();
        await until(() => button('Use Fixture private server'), 'the invite did not appear in the saved list');
        if (find('input[aria-label="Private Roblox invite link"]').value) throw new Error('The input retained a saved private link.');
        button('Use Fixture private server').click();
        await until(() => !find('input[placeholder="Enter a place ID or game link"]'), 'public and private targets were active together');
        find('button[aria-label="Rename Fixture private server"]').click();
        await until(() => find('input[aria-label="New private server name"]'), 'rename field did not open');
        type(find('input[aria-label="New private server name"]'), 'Renamed invite');
        await until(() => !button('Save name').disabled, 'rename was not ready to save');
        button('Save name').click();
        await until(() => find('.private-selection')?.textContent.includes('Renamed invite'), 'selected invite did not follow the rename');
        button('Launch private server').click();
        await until(() => launchedTarget?.reference?.id === 'invite-1', 'launch did not use a protected invite reference');
        if (launchedTarget.reference.revision !== 'r2' || launchedTarget.job || launchedTarget.placeId || JSON.stringify(launchedTarget).includes(inviteLink)) {
          throw new Error('Launch received an ambiguous destination or a raw invite.');
        }
        await until(() => !find('button[aria-label="Remove Renamed invite"]').disabled, 'the test launch did not finish');
        find('button[aria-label="Remove Renamed invite"]').click();
        await until(() => button('Remove invite'), 'remove confirmation did not open');
        button('Remove invite').click();
        await until(() => !button('Use Renamed invite') && button('Launch private server').disabled, 'removing the selected invite did not hold launch');
        if (find('input[placeholder="Enter a place ID or game link"]')) throw new Error('Removing the invite fell back to a public target.');
      } finally {
        button('Use a public game')?.click();
        find('button[aria-label="Accounts"]').click();
      }
    });
    await check('fatal-exit recovery is explicitly armed and its wait can be cancelled', async () => {
      const button = label => [...document.querySelectorAll('button')].find(element => element.textContent.trim() === label);
      find('button[aria-label="Sessions"]').click();
      try {
        const checkbox = () => [...document.querySelectorAll('label')].find(label => label.textContent.includes('Restart after a fatal exit'))?.querySelector('input');
        await until(() => checkbox(), 'no fatal-exit opt-in in Sessions');
        if (checkbox().checked) throw new Error('recovery was enabled by default');
        checkbox().click();
        button('Launch').click();
        await until(() => launchedTarget?.options?.recover === true, 'Launch did not carry the explicit recovery choice');
        sessionRows = [{
          attemptId: 'waiting-recovery', accountId: 'fixture-6', username: 'Fixture6', placeId: '1819',
          phase: 'retry_waiting', pid: null, requestedServer: null, observedServer: null, privateServer: false,
          detail: 'Windows reported a fatal exit. Automatic restart is waiting.',
        }];
        await until(() => button('Cancel restart'), 'the pending recovery had no cancel control');
        button('Cancel restart').click();
        await until(() => stoppedRecovery === 'waiting-recovery', 'Cancel did not target the waiting attempt');
      } finally {
        find('button[aria-label="Sessions"]').getAttribute('aria-current') === 'page'
          && find('button[aria-label="Accounts"]').click();
      }
    });
    await check('the support report retries a local export and displays the saved path', async () => {
      const button = () => [...document.querySelectorAll('button')].find(element => element.textContent.trim() === 'Save support report');
      find('button[aria-label="Overview"]').click();
      try {
        await until(() => button(), 'Overview does not offer a support report');
        button().click();
        const panel = () => find('[aria-label="Support report"]');
        await until(() => panel()?.querySelector('[role="alert"]'), 'export failure was not visible');
        button().click();
        await until(() => panel()?.querySelector('input[aria-label="Saved support report path"]')?.value.endsWith('zenith-report.json'),
          'the saved report path was missing');
        if (reportAttempts !== 2) throw new Error('The support export ran more than once per click.');
      } finally {
        find('button[aria-label="Accounts"]').click();
      }
    });
    await check('the Performance page requests and restores efficiency on the exact attempt', async () => {
      const button = label => [...document.querySelectorAll('button')].find(element => element.textContent.trim() === label);
      find('button[aria-label="Performance"]').click();
      try {
        await until(() => button('Request Windows efficiency'), 'the confirmed client has no efficiency control');
        button('Request Windows efficiency').click();
        await until(() => button('Restore system-managed efficiency'), 'readback did not expose the restore control');
        button('Restore system-managed efficiency').click();
        await until(() => button('Request Windows efficiency'), 'restoring did not clear the managed efficiency request');
        if (efficiencyChanges !== 2) throw new Error('Efficiency actions did not run exactly once.');
      } finally {
        find('button[aria-label="Accounts"]').click();
      }
    });
    await check('Windows minimizes and restores only the confirmed attempt and reports actual readback', async () => {
      const card = () => find('[aria-label="Roblox windows"] article');
      const button = label => [...(card()?.querySelectorAll('button') ?? [])].find(element => element.textContent.trim() === label);
      sessionRows = [{ attemptId: 'visible-window', accountId: 'fixture-6', username: 'Fixture6', placeId: '1819',
        phase: 'running', pid: 1777, requestedServer: null, observedServer: null, privateServer: false }];
      find('button[aria-label="Windows"]').click();
      try {
        await until(() => button('Minimize Roblox') && button('Restore Roblox'), 'the confirmed window has no visibility controls');
        macroPlayback = { id: 'fixture-window-macro', name: 'Fixture macro', account: 'Fixture6', phase: 'running', detail: '' };
        await until(() => button('Minimize Roblox').disabled && button('Restore Roblox').disabled, 'active foreground playback did not hold the window controls');
        macroPlayback = null;
        await until(() => !button('Minimize Roblox').disabled, 'completed playback did not release the window controls');
        button('Minimize Roblox').click();
        await until(() => find('[role="alert"]')?.textContent.includes('did not confirm'), 'a failed readback was hidden');
        if (card().textContent.includes('Minimize confirmed')) throw new Error('a failed action was presented as successful');
        button('Minimize Roblox').click();
        await until(() => card().textContent.includes('Minimize confirmed'), 'the observed minimize outcome was missing');
        button('Restore Roblox').click();
        await until(() => card().textContent.includes('Restore confirmed'), 'the observed restore outcome was missing');
        if (windowChanges.join(',') !== 'minimize,minimize,restore') throw new Error('unexpected window actions');
        sessionRows = [{ ...sessionRows[0], attemptId: 'unconfirmed-window', phase: 'unconfirmed' }];
        await until(() => !button('Minimize Roblox') && !button('Restore Roblox'), 'an unconfirmed attempt offered window actions');
      } finally {
        macroPlayback = null;
        sessionRows = [];
        find('button[aria-label="Accounts"]').click();
      }
    });
    await check('Panic holds launches across pages until cleanup and explicit resume', async () => {
      const button = (label) => [...document.querySelectorAll('button')].find(button => button.textContent.trim() === label);
      const stop = button('Panic stop');
      if (!stop) throw new Error('Panic stop is missing from Accounts');
      const bounds = stop.getBoundingClientRect();
      if (bounds.width === 0 || bounds.left < 0 || bounds.right > innerWidth) throw new Error('Panic stop is outside the visible toolbar');
      if (stop.getAttribute('aria-keyshortcuts') !== 'Control+Shift+P') throw new Error('Native shortcut status is missing');
      stop.click();
      await until(() => find('[aria-label="Panic stop status"]'), 'Panic hold did not appear');
      if (button('Resume launches')) throw new Error('Resume is visible before cleanup');
      find('button[aria-label="Sessions"]').click();
      await until(() => button('Launch')?.disabled, 'Panic did not hold the launch control');
      panic = { ...panic, canResume: true };
      await until(() => button('Resume launches'), 'Resume did not appear after cleanup');
      button('Resume launches').click();
      await until(() => button('Launch')?.disabled === false, 'Explicit resume did not rearm launching');
      button('Launch').click();
      await until(() => launchedEpoch === '4', 'A fresh launch did not include the resumed permit');
    });
    await check('Automation reads explicitly and clears a changed catalog page', async () => {
      const automation = find('button[aria-label="Automation"]');
      if (!automation) throw new Error('Automation is missing from navigation');
      automation.click();
      const panel = () => find('[aria-labelledby="macro-catalog-heading"]');
      const button = (label) => [...(panel()?.querySelectorAll('button') ?? [])].find(button => button.textContent.trim() === label);
      await until(() => button('Refresh catalog'), 'Saved macro catalog did not render');
      if (catalogRequests !== 0) throw new Error('Opening Automation read the host without an explicit refresh');
      button('Refresh catalog').click();
      await until(() => panel()?.querySelector('[role="status"]')?.textContent.includes('Open the Personal editor'), 'The disconnected state was not explained');
      button('Refresh catalog').click();
      await until(() => button('Next page') && panel()?.textContent.includes('Fixture macro 1'), 'Saved descriptors did not render');
      button('Next page').click();
      await until(() => panel()?.textContent.includes('The catalog changed'), 'A changed revision was not surfaced');
      if (panel()?.textContent.includes('Fixture macro 1') || button('Next page')) throw new Error('Stale rows survived the changed revision');
    });
    await check('Automation saves a pinned macro assignment', async () => {
      const assignments = () => find('[aria-labelledby="macro-assignments-heading"]');
      const button = (scope, label) => [...(scope?.querySelectorAll('button') ?? [])].find(button => button.textContent.trim() === label);
      if (!assignments()) throw new Error('Macro assignments are missing from Automation');
      const catalog = find('[aria-labelledby="macro-catalog-heading"]');
      button(catalog, 'Refresh catalog').click();
      await until(() => button(catalog, 'Select macro'), 'No saved macro could be selected');
      button(catalog, 'Select macro').click();
      await until(() => button(assignments(), 'Assign selected macro')?.disabled === false, 'Assignment stayed disabled');
      button(assignments(), 'Assign selected macro').click();
      await until(() => assignments()?.textContent.includes('Assignment saved.') && button(assignments(), 'Remove assignment'), 'Saved assignment did not render');
    });
    await check('Automation reviews the exact assignment and discards evidence from before Panic', async () => {
      const assignments = () => find('[aria-labelledby="macro-assignments-heading"]');
      const button = (label) => [...(assignments()?.querySelectorAll('button') ?? [])].find(button => button.textContent.trim() === label);
      const review = button('Review playback targets');
      if (!review || review.disabled) throw new Error('The saved assignment has no available target review');
      review.click();
      await until(() => assignments()?.textContent.includes('No active session') && reviewCalls === 1,
        'The exact recipient status did not render');
      if ([...assignments().querySelectorAll('button')].some(button => /^(run|play|start)$/i.test(button.textContent.trim()))) {
        throw new Error('Target review unexpectedly enabled macro execution');
      }
      readyWindow = true;
      review.click();
      await until(() => button('Start macro'), 'An eligible owned window did not expose paired playback');
      const code = assignments().querySelector('input[type="password"]');
      code.value = pairingCode;
      button('Start macro').click();
      await until(() => button('Pause macro'), 'Started operation did not appear');
      button('Pause macro').click();
      await until(() => button('Resume macro'), 'Paused operation did not appear');
      button('Resume macro').click();
      await until(() => button('Pause macro'), 'Resumed operation did not appear');
      button('Stop macro').click();
      await until(() => assignments().textContent.includes('cleanup finished') && !button('Stop macro'), 'Stop outcome did not appear');
      review.click();
      await until(() => reviewCalls === 3, 'The pending review did not start');
      [...document.querySelectorAll('button')].find(button => button.textContent.trim() === 'Panic stop').click();
      await until(() => find('[aria-label="Panic stop status"]'), 'The newer Panic hold did not appear');
      releaseReview();
      await until(() => !assignments()?.textContent.includes('Checking playback targets'), 'The review did not finish');
      if (assignments().textContent.includes('Owned window available') || button('Start macro') || !review.disabled) {
        throw new Error('Evidence from before Panic survived the hold');
      }
      button('Remove assignment').click();
      await until(() => assignments()?.textContent.includes('Unassigned') && !button('Remove assignment')
        && !assignments()?.textContent.includes('No active session'), 'Removed assignment or review stayed visible');
    });
    const result = document.createElement('output');
    result.id = 'account-fixture-result';
    result.textContent = checks.join('\n');
    document.body.append(result);
  }
  document.addEventListener('DOMContentLoaded', () => void run());
}

const dist = resolve(process.argv[2] ?? 'dist');
const browser = process.env.ZENITH_FIXTURE_BROWSER ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const scratchParent = await realpath(tmpdir());
const prefix = 'zenith-account-fixture-';
const profile = await mkdtemp(join(scratchParent, prefix));
const mime = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html' };
const server = createServer(async (request, response) => {
  try {
    const { pathname, search } = new URL(request.url, 'http://127.0.0.1');
    if (pathname === '/fixture.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(`(${exerciseWorkspace.toString()})()`);
    } else if (pathname === '/headshot.svg') {
      if (search === '?fixture-2') {
        response.writeHead(404, { 'Cache-Control': 'public, max-age=600' }).end();
        return;
      }
      response.writeHead(200, { 'Content-Type': 'image/svg+xml' });
      response.end('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><circle cx="16" cy="16" r="16" fill="teal"/></svg>');
    } else if (pathname === '/') {
      const html = await readFile(join(dist, 'index.html'), 'utf8');
      response.writeHead(200, { 'Content-Type': 'text/html' });
      response.end(html.replace('<head>', '<head><script src="/fixture.js"></script>'));
    } else if (/^\/assets\/[\w.-]+\.(js|css)$/.test(pathname)) {
      const asset = await readFile(join(dist, pathname.slice(1)));
      response.writeHead(200, { 'Content-Type': mime[extname(pathname)] });
      response.end(asset);
    } else {
      response.writeHead(404).end();
    }
  } catch { response.writeHead(500).end(); }
});

try {
  await new Promise((ready, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', ready);
  });
  const { stdout } = await promisify(execFile)(browser, [
    '--headless=new', '--disable-background-networking', '--disable-sync', '--no-first-run', '--no-default-browser-check',
    `--user-data-dir=${profile}`, '--dump-dom', '--virtual-time-budget=27000',
    `http://127.0.0.1:${server.address().port}/`,
  ], { timeout: 30000, windowsHide: true, maxBuffer: 1024 * 1024 });
  const result = stdout.match(/<output id="account-fixture-result">([^<]*)<\/output>/)?.[1];
  if (!result) throw new Error('The browser did not finish the account fixture.');
  console.log(result);
  if (result.split('\n').some(line => !line.startsWith('PASS '))) process.exitCode = 1;
} finally {
  server.close();
  const target = await realpath(profile);
  if (target === profile && dirname(target) === scratchParent && basename(target).startsWith(prefix)) {
    await rm(target, { recursive: true, force: true, maxRetries: 5 });
  }
}
