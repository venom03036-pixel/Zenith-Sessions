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
  let headshotRequests = 0;
  let releaseFirstProfile;
  const firstProfile = new Promise(resolve => { releaseFirstProfile = resolve; });
  window.__TAURI_INTERNALS__ = {
    invoke: async (command, { id } = {}) => {
      switch (command) {
        case 'workspace_snapshot': return {
          accounts, sessions: [], launch: { active: 0, pairEnabled: false, hold: null },
          admission: null, recovery: { issue: null, attempts: [] },
          signInAttempt: null, signInError: null, savedSignIn,
        };
        case 'legacy_workspace': return null;
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
    await check('pending headshot is retried and rendered', async () => {
      await until(() => row(6)?.querySelector('img:not([hidden])')?.naturalWidth > 0,
        'sixth headshot stayed on initials');
      if (headshotRequests !== 2) throw new Error(`expected 2 requests, got ${headshotRequests}`);
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
    const { pathname } = new URL(request.url, 'http://127.0.0.1');
    if (pathname === '/fixture.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(`(${exerciseWorkspace.toString()})()`);
    } else if (pathname === '/headshot.svg') {
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
    `--user-data-dir=${profile}`, '--dump-dom', '--virtual-time-budget=16000',
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
