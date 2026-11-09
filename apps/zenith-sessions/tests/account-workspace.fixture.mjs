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
  let panic = { epoch: '0', held: false, canResume: false, shortcutAvailable: true, error: null };
  let launchedEpoch;
  let catalogRequests = 0;
  const catalogCursor = { offset: 25, hostSessionId: 'fixture-session', catalogRevision: '9007199254740993', total: 26 };
  const selectionPage = { ...catalogCursor, offset: 0 };
  const emptyCatalog = { state: 'Disconnected', items: [], total: 0, offset: 0, hostVersion: '', previous: null, next: null };
  const assignmentTarget = { kind: 'Account', id: 'fixture-6' };
  let assignment = null;
  let assignmentStamp = 'initial-stamp';
  const assignmentView = () => ({ stamp: assignmentStamp, total: 1, offset: 0, previous: null, next: null,
    unreadableLegacy: 0, rows: [{ target: assignmentTarget, name: 'Fixture6', editable: true, inherited: false, assignment }] });
  localStorage.setItem('zenith-preview-place', '1818');
  let headshotRequests = 0;
  let releaseFirstProfile;
  const firstProfile = new Promise(resolve => { releaseFirstProfile = resolve; });
  window.__TAURI_INTERNALS__ = {
    invoke: async (command, { id, epoch, cursor, stamp, target, edit } = {}) => {
      switch (command) {
        case 'workspace_snapshot': return {
          accounts, sessions: [], launch: { active: 0, pairEnabled: false, hold: null },
          admission: { limitingResource: null, physicalAvailable: 1, physicalRequired: 1,
            commitAvailable: 1, commitRequired: 1, canTryOne: false },
          recovery: { issue: null, attempts: [] }, panic,
          signInAttempt: null, signInError: null, savedSignIn,
        };
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
          panic = { ...panic, epoch: '3', held: true };
          return;
        case 'resume_launches':
          if (epoch !== '3' || !panic.canResume) throw new Error('The fixture requires confirmed cleanup.');
          panic = { ...panic, epoch: '4', held: false, canResume: false };
          return;
        case 'launch_account':
          if (panic.held || epoch !== panic.epoch) throw new Error('The fixture rejected a stale launch permit.');
          launchedEpoch = epoch;
          return;
        case 'account_headshots':
          headshotRequests++;
          return Object.fromEntries(accounts.slice(0, headshotRequests === 1 ? 5 : 6)
            .map(account => [account.profileId, `/headshot.svg?${account.profileId}`]));
        case 'account_profile': {
          if (id === 'fixture-1') await firstProfile;
          return { userId: id, displayName: `${id} display name`, created: '2020-01-01', description: '' };
        }
        case 'account_favorites': return { items: [{ placeId: '12345', name: `${id} favorite` }], nextCursor: null };
        case 'account_private_profile': return {
          balance: { Ok: savedSignIn?.profileId === id ? '73' : '0' },
          friends: { Ok: { items: [{ id: '42', name: '', displayName: `${id} friend` }], nextCursor: null } },
        };
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
      savedSignIn = { profileId: 'fixture-6', attempt: 'renewed-fixture' };
      await until(() => /Robux73/.test(details()), 'renewed account still showed the old balance');
      if (!document.querySelector('.sign-in-saved')?.textContent.includes('@Fixture6')) {
        throw new Error('a successful save did not identify the account that was saved');
      }
    });
    await check('all six saved accounts show their own headshot and details', async () => {
      for (let index = 1; index <= accounts.length; index++) {
        row(index).click();
        const id = `fixture-${index}`;
        await until(() => row(index).getAttribute('aria-pressed') === 'true'
          && details().includes(`${id} display name`) && details().includes(`${id} favorite`)
          && details().includes(`${id} friend`) && details().includes(index === 6 ? 'Robux73' : 'Robux0')
          && find('[aria-label="Account details"] img:not([hidden])')?.src.endsWith(`?${id}`),
        `account ${index} showed missing or mismatched details`);
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
    await check('Automation saves a pinned macro and explicitly removes its assignment', async () => {
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
      button(assignments(), 'Remove assignment').click();
      await until(() => assignments()?.textContent.includes('Unassigned') && !button(assignments(), 'Remove assignment'), 'Removed assignment stayed visible');
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
let failSecondHeadshot = true;
const server = createServer(async (request, response) => {
  try {
    const { pathname, search } = new URL(request.url, 'http://127.0.0.1');
    if (pathname === '/fixture.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(`(${exerciseWorkspace.toString()})()`);
    } else if (pathname === '/headshot.svg') {
      if (search === '?fixture-2' && failSecondHeadshot) {
        failSecondHeadshot = false;
        response.writeHead(404, { 'Cache-Control': 'no-store' }).end();
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
