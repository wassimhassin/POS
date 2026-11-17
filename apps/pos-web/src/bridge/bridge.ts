import {
  BRIDGE_PROTOCOL_VERSION,
  type BridgeCommand,
  type BridgeCommands,
  type BridgeResponse,
  type ClientLogLevel,
} from './contracts';

interface WebViewMessaging {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewMessaging };
  }
}

const REQUEST_TIMEOUT_MS = 10_000;

export class BridgeError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.name = 'BridgeError';
    this.code = code;
  }
}

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const pending = new Map<string, Pending>();
let listening = false;

function webview(): WebViewMessaging | undefined {
  return window.chrome?.webview;
}

/** True when running inside the WPF/WebView2 host. */
export function isHosted(): boolean {
  return webview() !== undefined;
}

export function isBridgeResponse(value: unknown): value is BridgeResponse {
  if (typeof value !== 'object' || value === null) return false;
  const v = value as Record<string, unknown>;
  if (typeof v.id !== 'string' || typeof v.ok !== 'boolean') return false;
  if (v.ok) return 'result' in v;
  const err = v.error as Record<string, unknown> | undefined;
  return typeof err === 'object' && err !== null && typeof err.code === 'string' && typeof err.message === 'string';
}

function ensureListener(wv: WebViewMessaging): void {
  if (listening) return;
  wv.addEventListener('message', (event) => {
    const msg = event.data;
    if (!isBridgeResponse(msg)) return; // ignore anything that is not a correlated response
    const entry = pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);
    clearTimeout(entry.timer);
    if (msg.ok) entry.resolve(msg.result);
    else entry.reject(new BridgeError(msg.error.code, msg.error.message));
  });
  listening = true;
}

/** Sends an allowlisted command to the C# host and resolves with its typed result. */
export function call<K extends BridgeCommand>(
  type: K,
  payload: BridgeCommands[K]['request'],
): Promise<BridgeCommands[K]['response']> {
  const wv = webview();
  if (!wv) return Promise.reject(new BridgeError('not_hosted', "L'application doit être lancée depuis le poste POS."));
  ensureListener(wv);

  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new BridgeError('timeout', `Le poste n'a pas répondu (${type}).`));
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
    wv.postMessage({ v: BRIDGE_PROTOCOL_VERSION, id, type, payload });
  });
}

/** Fire-and-forget log forwarding to the host's structured log. Never throws. */
export function logToHost(level: ClientLogLevel, message: string, detail?: string): void {
  if (!isHosted()) {
    console[level === 'info' ? 'log' : level](message, detail ?? '');
    return;
  }
  const payload = detail === undefined ? { level, message: message.slice(0, 2000) } : { level, message: message.slice(0, 2000), detail: detail.slice(0, 8000) };
  call('log.client', payload).catch(() => {
    /* logging must never break the UI */
  });
}
