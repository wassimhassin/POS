// End-to-end check that the draft order survives application restarts.
//
// Launches the real WPF app (isolated data dir via POS_DATA_DIR), drives the React UI through the
// WebView2 DevTools protocol (enabled for this process only via WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS),
// then closes/kills and relaunches the app and checks the cart was restored from SQLite.
//
// Usage: node tests/e2e/restart-persistence.mjs [path\to\Pos.Desktop.exe]
// Requires Node >= 22 (global fetch/WebSocket). Windows only.

import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const exe = path.resolve(
  process.argv[2] ?? path.join(repoRoot, 'apps/windows-pos/Pos.Desktop/bin/Debug/net10.0-windows/Pos.Desktop.exe'),
);
const UI_ORIGIN = 'https://pos-app.example';
const port = 9300 + Math.floor(Math.random() * 500);
const dataDir = mkdtempSync(path.join(tmpdir(), 'pos-e2e-'));
const spawned = [];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (msg) => console.log(`[e2e] ${msg}`);

function assertEqual(actual, expected, label) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${label}: expected ${e}, got ${a}`);
  log(`OK  ${label}: ${a}`);
}

async function waitUntil(fn, label, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    try {
      last = await fn();
      if (last) return last;
    } catch (e) {
      last = e;
    }
    await sleep(150);
  }
  throw new Error(`Timed out waiting for ${label} (last: ${last instanceof Error ? last.message : JSON.stringify(last)})`);
}

class Cdp {
  static async connect(wsUrl) {
    const cdp = new Cdp(new WebSocket(wsUrl));
    await new Promise((resolve, reject) => {
      cdp.ws.addEventListener('open', resolve, { once: true });
      cdp.ws.addEventListener('error', () => reject(new Error('CDP connection failed')), { once: true });
    });
    return cdp;
  }

  constructor(ws) {
    this.ws = ws;
    this.nextId = 1;
    this.pending = new Map();
    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error.message));
      else p.resolve(msg.result);
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const res = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (res.exceptionDetails) throw new Error(res.exceptionDetails.exception?.description ?? res.exceptionDetails.text);
    return res.result.value;
  }

  close() {
    this.ws.close();
  }
}

async function launch() {
  const child = spawn(exe, [], {
    env: {
      ...process.env,
      POS_DATA_DIR: dataDir,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
    },
    stdio: 'ignore',
  });
  spawned.push(child);
  const exited = new Promise((resolve) => child.once('exit', (code) => resolve(code)));
  const target = await waitUntil(async () => {
    const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    return list.find((t) => t.type === 'page' && t.url.startsWith(UI_ORIGIN));
  }, 'WebView2 page target');
  const cdp = await Cdp.connect(target.webSocketDebuggerUrl);
  await waitUntil(
    () => cdp.evaluate(`document.querySelectorAll('[data-testid="product-card"]').length === 12`),
    'product grid',
  );
  log(`App started (pid ${child.pid})`);
  return { child, exited, cdp };
}

async function stop(app, { force }) {
  app.cdp.close();
  execFileSync('taskkill', force ? ['/F', '/T', '/PID', String(app.child.pid)] : ['/PID', String(app.child.pid)], {
    stdio: 'ignore',
  });
  const code = await Promise.race([app.exited, sleep(15_000).then(() => 'timeout')]);
  if (code === 'timeout') throw new Error('App did not exit');
  log(`App ${force ? 'force-killed' : 'closed'} (exit code ${code})`);
  // Let WebView2 child processes release the port and the user data folder.
  await waitUntil(async () => {
    try {
      await fetch(`http://127.0.0.1:${port}/json/version`);
      return false;
    } catch {
      return true;
    }
  }, 'debug port release');
}

const cartState = (cdp) =>
  cdp.evaluate(`[...document.querySelectorAll('[data-testid="cart-line"]')]
      .map((e) => [e.dataset.productId, Number(e.dataset.quantity)])`);

const total = (cdp) => cdp.evaluate(`document.querySelector('[data-testid="cart-total"]').textContent.replace(/\\s/g, ' ')`);

async function tap(cdp, productId) {
  await cdp.evaluate(`document.querySelector('[data-testid="product-card"][data-product-id="${productId}"]').click()`);
}

async function waitSaved(cdp) {
  await sleep(400); // let the save start before polling for its completion
  await waitUntil(() => cdp.evaluate(`document.querySelector('[data-save-status]').dataset.saveStatus === 'saved'`), 'save');
}

async function main() {
  if (!existsSync(exe)) throw new Error(`Executable not found: ${exe}. Build first (scripts/build.ps1).`);
  log(`exe: ${exe}`);
  log(`data dir: ${dataDir}`);

  // Run 1: fresh database, build a cart, close normally.
  let app = await launch();
  assertEqual(await cartState(app.cdp), [], 'run 1: cart starts empty');
  await tap(app.cdp, 'p-espresso');
  await tap(app.cdp, 'p-espresso');
  await tap(app.cdp, 'p-croissant');
  await waitSaved(app.cdp);
  const expected1 = [['p-espresso', 2], ['p-croissant', 1]];
  assertEqual(await cartState(app.cdp), expected1, 'run 1: cart after taps');
  assertEqual(await total(app.cdp), '7,000 DT', 'run 1: total');
  await stop(app, { force: false });

  // Run 2: restored after a normal restart; add an item, then hard-kill the process.
  app = await launch();
  assertEqual(await cartState(app.cdp), expected1, 'run 2: cart restored after restart');
  await tap(app.cdp, 'p-pizza');
  await waitSaved(app.cdp);
  await stop(app, { force: true });

  // Run 3: acknowledged save survived the forced kill; clear the order.
  app = await launch();
  const expected3 = [...expected1, ['p-pizza', 1]];
  assertEqual(await cartState(app.cdp), expected3, 'run 3: cart restored after forced kill');
  assertEqual(await total(app.cdp), '21,000 DT', 'run 3: total');
  await app.cdp.evaluate(`[...document.querySelectorAll('button')].find((b) => b.textContent.includes('Vider')).click()`);
  await waitSaved(app.cdp);
  await stop(app, { force: false });

  // Run 4: cleared order stays cleared.
  app = await launch();
  assertEqual(await cartState(app.cdp), [], 'run 4: cleared cart stays empty');
  await stop(app, { force: false });

  // Structured logs were written and contain no errors.
  const logDir = path.join(dataDir, 'logs');
  const entries = readdirSync(logDir)
    .flatMap((f) => readFileSync(path.join(logDir, f), 'utf8').split('\n'))
    .filter(Boolean)
    .map((l) => JSON.parse(l));
  assertEqual(entries.filter((e) => e.message.startsWith('Application starting')).length, 4, 'log: 4 startups recorded');
  const errors = entries.filter((e) => e.level === 'Error' || e.level === 'Critical');
  assertEqual(errors.map((e) => e.message), [], 'log: no errors');

  log('PASS');
}

let exitCode = 0;
try {
  await main();
} catch (e) {
  console.error(`[e2e] FAIL: ${e.message}`);
  exitCode = 1;
} finally {
  for (const child of spawned) {
    if (child.exitCode !== null) continue;
    try {
      execFileSync('taskkill', ['/F', '/T', '/PID', String(child.pid)], { stdio: 'ignore' });
    } catch {
      /* already gone */
    }
  }
  // WebView2 helper processes can hold the user-data folder for a moment after exit.
  for (let attempt = 0; attempt < 20; attempt++) {
    try {
      rmSync(dataDir, { recursive: true, force: true });
      break;
    } catch (e) {
      if (attempt === 19) console.warn(`[e2e] could not remove ${dataDir}: ${e.message}`);
      else await sleep(500);
    }
  }
}
process.exit(exitCode);
