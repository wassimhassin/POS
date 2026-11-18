// Shared helpers for the end-to-end tests (Windows only, Node >= 22 for global fetch/WebSocket).
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const UI_ORIGIN = 'https://pos-app.example';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const log = (msg) => console.log(`[e2e] ${msg}`);

export function desktopExe(configuration, override) {
  const exe = path.resolve(
    override ?? path.join(repoRoot, `apps/windows-pos/Pos.Desktop/bin/${configuration}/net10.0-windows/Pos.Desktop.exe`),
  );
  if (!existsSync(exe)) throw new Error(`Executable not found: ${exe}. Build first (scripts/build.ps1).`);
  return exe;
}

export function gatewayExe(configuration = 'Debug') {
  const exe = path.join(repoRoot, `apps/branch-gateway/Pos.Gateway/bin/${configuration}/net10.0-windows/Pos.Gateway.exe`);
  if (!existsSync(exe)) throw new Error(`Gateway not found: ${exe}. Build first (scripts/build.ps1).`);
  return exe;
}

/** Runs a gateway CLI command (init, enroll, …) and returns its standard output. */
export function gatewayCli(exe, dataDir, ...args) {
  return execFileSync(exe, [...args, '--data-dir', dataDir], { encoding: 'utf8' });
}

export async function waitForTcp(port, label, timeoutMs = 30_000) {
  const { connect } = await import('node:net');
  return waitUntil(
    () =>
      new Promise((resolve) => {
        const socket = connect(port, '127.0.0.1');
        socket.once('connect', () => {
          socket.destroy();
          resolve(true);
        });
        socket.once('error', () => resolve(false));
      }),
    label,
    timeoutMs,
  );
}

/** Sets a React-controlled input's value the way a user typing would. */
export function fill(cdp, testId, value) {
  return cdp.evaluate(`(() => {
    const el = document.querySelector('[data-testid="${testId}"]');
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(value)});
    el.dispatchEvent(new Event('input', { bubbles: true }));
  })()`);
}

/** Calls the host bridge from the page with a private listener (same allowlisted protocol as the UI). */
export function bridge(cdp, type, payload) {
  return cdp.evaluate(`new Promise((resolve) => {
    const id = crypto.randomUUID();
    const handler = (e) => {
      if (e.data && e.data.id === id) { window.chrome.webview.removeEventListener('message', handler); resolve(e.data); }
    };
    window.chrome.webview.addEventListener('message', handler);
    window.chrome.webview.postMessage({ v: 1, id, type: ${JSON.stringify(type)}, payload: ${JSON.stringify(payload)} });
  })`);
}

export function randomPort() {
  return 9300 + Math.floor(Math.random() * 500);
}

export function assertEqual(actual, expected, label) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${label}: expected ${e}, got ${a}`);
  log(`OK  ${label}: ${a}`);
}

export function assert(condition, label, detail = '') {
  if (!condition) throw new Error(`${label} ${detail}`);
  log(`OK  ${label}`);
}

export async function waitUntil(fn, label, timeoutMs = 30_000) {
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

export async function isPortOpen(port) {
  try {
    await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(1000) });
    return true;
  } catch {
    return false;
  }
}

export class Cdp {
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

/** Isolated data dir + tracking of spawned processes, with best-effort cleanup. */
export class TestSession {
  constructor() {
    this.dataDir = mkdtempSync(path.join(tmpdir(), 'pos-e2e-'));
    this.spawned = [];
  }

  /** Starts the app. With `port`, enables WebView2 remote debugging via the environment. */
  start(exe, { port, args = [] } = {}) {
    const env = { ...process.env, POS_DATA_DIR: this.dataDir };
    if (port) env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = `--remote-debugging-port=${port}`;
    const child = spawn(exe, args, { env, stdio: 'ignore' });
    this.spawned.push(child);
    const exited = new Promise((resolve) => child.once('exit', (code) => resolve(code)));
    return { child, exited };
  }

  /** Starts the app and attaches over CDP once the product grid is rendered. */
  async launch(exe, port) {
    const app = this.start(exe, { port });
    const target = await waitUntil(async () => {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      return list.find((t) => t.type === 'page' && t.url.startsWith(UI_ORIGIN));
    }, 'WebView2 page target');
    app.cdp = await Cdp.connect(target.webSocketDebuggerUrl);
    app.port = port;
    await waitUntil(
      () => app.cdp.evaluate(`document.querySelectorAll('[data-testid="product-card"]').length === 12`),
      'product grid',
    );
    log(`App started (pid ${app.child.pid})`);
    return app;
  }

  async stop(app, { force }) {
    app.cdp?.close();
    execFileSync('taskkill', force ? ['/F', '/T', '/PID', String(app.child.pid)] : ['/PID', String(app.child.pid)], {
      stdio: 'ignore',
    });
    const code = await Promise.race([app.exited, sleep(15_000).then(() => 'timeout')]);
    if (code === 'timeout') throw new Error('App did not exit');
    log(`App ${force ? 'force-killed' : 'closed'} (exit code ${code})`);
    if (app.port) await waitUntil(async () => !(await isPortOpen(app.port)), 'debug port release');
  }

  logEntries() {
    const logDir = path.join(this.dataDir, 'logs');
    if (!existsSync(logDir)) return [];
    return readdirSync(logDir)
      .flatMap((f) => readFileSync(path.join(logDir, f), 'utf8').split('\n'))
      .filter(Boolean)
      .map((l) => JSON.parse(l));
  }

  /** Recursively lists file names under the data dir (used to look for DevToolsActivePort). */
  findFiles(name, dir = this.dataDir) {
    const found = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) found.push(...this.findFiles(name, full));
      else if (entry.name === name) found.push(full);
    }
    return found;
  }

  async cleanup() {
    for (const child of this.spawned) {
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
        rmSync(this.dataDir, { recursive: true, force: true });
        return;
      } catch (e) {
        if (attempt === 19) console.warn(`[e2e] could not remove ${this.dataDir}: ${e.message}`);
        else await sleep(500);
      }
    }
  }
}

/** Runs a test body with a fresh session and sets the process exit code. */
export async function runTest(name, body) {
  const session = new TestSession();
  log(`${name} — data dir: ${session.dataDir}`);
  let exitCode = 0;
  try {
    await body(session);
    log(`PASS ${name}`);
  } catch (e) {
    console.error(`[e2e] FAIL ${name}: ${e.message}`);
    exitCode = 1;
  } finally {
    await session.cleanup();
  }
  process.exitCode = exitCode;
}
