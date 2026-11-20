// End-to-end: orders taken on a real POS desktop terminal during a cloud outage eventually appear in the cloud
// exactly once (logical records), across a gateway crash and a lost cloud acknowledgment.
//
//   throwaway PostgreSQL cluster → cloud migrate / provision org → business → branch (gateway's own id)
//   → gateway linked with a one-time code → POS paired through its UI → order synced
//   → cloud killed → orders taken offline → gateway hard-killed and restarted mid-outage → more orders
//   → cloud restarted with its first acknowledgment dropped after commit (forces a replay)
//   → cloud holds every order once, event log = gateway outbox, replay reported as duplicates.
//
// Requires a Debug build (fault injection) and a local PostgreSQL installation (binaries only; the
// installed service is not used). Usage: node tests/e2e/cloud-sync.mjs

import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
process.removeAllListeners('warning');
const { DatabaseSync } = await import('node:sqlite');
import {
  assert,
  assertEqual,
  bridge,
  cloudExe,
  desktopExe,
  fill,
  gatewayCli,
  gatewayExe,
  httpsGetJson,
  log,
  randomPort,
  runTest,
  sleep,
  startPostgres,
  waitForTcp,
  waitUntil,
} from './lib.mjs';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';

const posExe = desktopExe('Debug', process.argv[2]);
const gwExe = gatewayExe('Debug');
const clExe = cloudExe('Debug');
const gwPort = 7600 + Math.floor(Math.random() * 300);
const cloudPort = gwPort + 400;
const posPort = randomPort();

const $ = (cdp, js) => cdp.evaluate(js);
const click = (cdp, selector) => $(cdp, `document.querySelector(${JSON.stringify(selector)}).click()`);
const state = (cdp) => $(cdp, `document.querySelector('[data-testid="gateway-status"]').dataset.state`);

async function ok(cdp, type, payload) {
  const res = await bridge(cdp, type, payload);
  if (!res.ok) throw new Error(`${type}: ${res.error.code} ${res.error.message}`);
  return res.result;
}

/** One order through the real terminal: create, add items, optionally submit. */
async function placeOrder(cdp, products, submit) {
  let { order } = await ok(cdp, 'shared.create', { operationId: crypto.randomUUID(), label: null });
  for (const productId of products) {
    ({ order } = await ok(cdp, 'shared.addItem', { operationId: crypto.randomUUID(), orderId: order.id, productId, quantity: 1 }));
  }
  if (submit) ({ order } = await ok(cdp, 'shared.submit', { operationId: crypto.randomUUID(), orderId: order.id, expectedVersion: order.version }));
  return order;
}

await runTest('cloud-sync', async (session) => {
  const dir = session.dataDir;
  const gwDir = path.join(dir, 'gateway');
  const pg = await startPostgres(dir);
  const certPassword = randomBytes(18).toString('base64url');
  const mailDir = path.join(dir, 'mail');
  mkdirSync(mailDir, { recursive: true });
  const cloudEnv = {
    ...process.env,
    POS_CLOUD_DB: pg.app,
    POS_CLOUD_MIGRATION_DB: pg.owner,
    POS_CLOUD_CERT_PASSWORD: certPassword,
    POS_CLOUD_PUBLIC_ORIGIN: 'https://dashboard.test',
    POS_CLOUD_MAIL_DIR: mailDir,
    POS_CLOUD_DATA_KEYS: `e2e1:${randomBytes(32).toString('base64')}`,
  };
  const cloudCli = (...args) => {
    const out = execFileSync(clExe, args, { encoding: 'utf8', env: cloudEnv });
    return Object.fromEntries(out.split('\n').filter((l) => l.includes('=')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
  };

  let cloud;
  const cloudLog = [];
  const startCloud = async (fault) => {
    cloud = spawn(clExe, ['run', '--listen', `https://127.0.0.1:${cloudPort}`, '--certificate', path.join(dir, 'cloud.pfx')], {
      env: { ...cloudEnv, ...(fault ? { POS_CLOUD_FAULT: fault } : {}) },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    session.spawned.push(cloud);
    for (const stream of [cloud.stdout, cloud.stderr]) stream.on('data', (d) => cloudLog.push(...d.toString().split('\n').filter(Boolean)));
    await waitForTcp(cloudPort, 'cloud listening');
    log(`Cloud API started (pid ${cloud.pid}${fault ? `, fault ${fault}` : ''})`);
  };
  const killCloud = async () => {
    execFileSync('taskkill', ['/F', '/PID', String(cloud.pid)], { stdio: 'ignore' });
    await waitUntil(() => cloud.exitCode !== null || cloud.signalCode !== null, 'cloud stopped');
    log('Cloud API killed (outage begins)');
  };

  let gateway;
  const startGateway = async () => {
    gateway = spawn(gwExe, ['run', '--data-dir', gwDir, '--listen', `https://127.0.0.1:${gwPort}`, '--sync-max-backoff', '2', '--sync-timeout', '5'], { stdio: 'ignore' });
    session.spawned.push(gateway);
    await waitForTcp(gwPort, 'gateway listening');
    log(`Gateway started (pid ${gateway.pid})`);
  };
  const gw = (sql, ...params) => {
    const conn = new DatabaseSync(path.join(gwDir, 'gateway.db'), { readOnly: true });
    try {
      return conn.prepare(sql).all(...params);
    } finally {
      conn.close();
    }
  };
  const gwValue = (sql, ...params) => Object.values(gw(sql, ...params)[0])[0];
  const synced = () => {
    const outbox = gwValue('SELECT COALESCE(MAX(sequence), 0) FROM sync_outbox');
    return outbox > 0 && gwValue('SELECT acked_sequence FROM sync_state') === outbox && Number(pg.psql('SELECT COALESCE(MAX(last_sequence), 0) FROM branch_sync_states')) === outbox;
  };

  try {
    // --- Cloud provisioning (cloud operator) ---
    assert(cloudCli('migrate').schema.includes('InitialCloudSchema'), 'cloud schema migrated by the owner role');
    pg.grantApp();
    const pin = cloudCli('dev-cert', '--out', path.join(dir, 'cloud.pfx'), '--host', '127.0.0.1').sha256;
    const ca = readFileSync(path.join(dir, 'cloud.pem'));

    // --- Branch gateway, existing identity kept: the cloud branch is created with the gateway's id ---
    const branchId = crypto.randomUUID();
    gatewayCli(gwExe, gwDir, 'init', '--branch-id', branchId, '--branch-name', 'Succursale E2E');
    const orgId = cloudCli('org-create', '--name', 'Groupe E2E').organization_id;
    const businessId = cloudCli('business-create', '--org', orgId, '--name', 'Café E2E').business_id;
    assertEqual(cloudCli('branch-create', '--business', businessId, '--name', 'Succursale E2E', '--id', branchId).branch_id, branchId, 'cloud branch uses the gateway branch id');
    const { code } = cloudCli('gateway-code', '--branch', branchId);

    await startCloud();
    await startGateway();
    gatewayCli(gwExe, gwDir, 'cloud-link', '--url', `https://127.0.0.1:${cloudPort}`, '--code', code, '--pin', pin);
    await waitUntil(() => gwValue('SELECT gateway_id IS NOT NULL FROM cloud_link') === 1, 'gateway registered with the cloud', 45_000);
    assertEqual(gwValue('SELECT organization_id FROM cloud_link'), orgId, 'tenant ids come from the cloud, not from the gateway');

    // --- POS terminal paired through its UI ---
    const pos = await session.launch(posExe, posPort);
    const pairing = gatewayCli(gwExe, gwDir, 'enroll', '--name', 'Caisse E2E');
    await click(pos.cdp, '[data-testid="gateway-status"]');
    await fill(pos.cdp, 'gw-url', `https://127.0.0.1:${gwPort}`);
    await fill(pos.cdp, 'gw-fingerprint', /Empreinte\s+: (\S+)/.exec(pairing)[1]);
    await fill(pos.cdp, 'gw-code', /Code d'appairage : (\S+)/.exec(pairing)[1]);
    await click(pos.cdp, '[data-testid="gw-enroll"]');
    await waitUntil(async () => (await state(pos.cdp)) === 'connected', 'POS connected to the gateway');

    // --- 1. Online: an order taken in the UI reaches the cloud ---
    await click(pos.cdp, '[data-testid="mode-shared"]');
    await click(pos.cdp, '[data-testid="shared-new"]');
    const first = await waitUntil(() => $(pos.cdp, `document.querySelector('[data-testid="shared-order"]')?.dataset.orderId`), 'UI: order created');
    await click(pos.cdp, '[data-product-id="p-espresso"]');
    await waitUntil(() => $(pos.cdp, `!!document.querySelector('[data-testid="shared-line"][data-product-id="p-espresso"]')`), 'UI: espresso added');
    await click(pos.cdp, '[data-testid="shared-submit"]');
    await waitUntil(() => $(pos.cdp, `document.querySelector('[data-testid="shared-order"]')?.dataset.status === 'submitted'`), 'UI: submitted');
    await waitUntil(synced, 'online order synchronized', 30_000);
    assertEqual(pg.psql(`SELECT status FROM orders WHERE id = '${first}'`), 'submitted', 'cloud has the submitted order');

    // --- 2. Cloud outage: the branch keeps working ---
    await killCloud();
    const offline = [await placeOrder(pos.cdp, ['p-pizza', 'p-croissant'], true), await placeOrder(pos.cdp, ['p-espresso'], false)];
    log(`${offline.length} orders taken during the outage`);

    // --- 3. Gateway hard crash during the outage, restart, more orders ---
    execFileSync('taskkill', ['/F', '/PID', String(gateway.pid)], { stdio: 'ignore' });
    await waitUntil(async () => (await state(pos.cdp)) === 'offline', 'POS sees the gateway offline');
    await startGateway();
    await waitUntil(async () => (await state(pos.cdp)) === 'connected', 'POS reconnected after the gateway restart', 45_000);
    offline.push(await placeOrder(pos.cdp, ['p-croissant', 'p-croissant'], true));
    await sleep(3000); // several failed sync attempts against the dead cloud
    const pending = gwValue('SELECT COUNT(*) FROM sync_outbox WHERE acked_at IS NULL');
    assert(pending >= 7, `outbox holds the unsent operations durably (${pending} pending)`);
    assert(gwValue('SELECT consecutive_failures FROM sync_state') > 0 && gwValue('SELECT last_error FROM sync_state'), 'gateway reports the failed attempts');
    assertEqual(Number(pg.psql(`SELECT COUNT(*) FROM orders`)), 1, 'cloud still has only the pre-outage order');

    // --- 4. Recovery, with the first acknowledgment lost after commit ---
    const beforeRecovery = gw('SELECT * FROM shared_orders ORDER BY number');
    const linesBefore = gw('SELECT * FROM shared_order_lines ORDER BY order_id, product_id');
    await startCloud('drop-ack:1');
    await waitUntil(synced, 'backlog synchronized after recovery', 60_000);
    assert(cloudLog.some((l) => l.includes('acknowledgment dropped after commit')), 'cloud dropped an acknowledgment after committing');
    const replayed = cloudLog.filter((l) => /\b[1-9]\d* duplicates \(already applied/.test(l));
    assert(replayed.length > 0, `the gateway replayed the unacknowledged batch; cloud recognised it as duplicates (${replayed.length} batch(es))`);

    // --- 5. Exactly once at the logical-record level ---
    const gwOrders = gw(`SELECT id, number, status, version FROM shared_orders ORDER BY number`);
    const outboxCount = gwValue('SELECT COUNT(*) FROM sync_outbox');
    assertEqual(Number(pg.psql('SELECT COUNT(*) FROM orders')), gwOrders.length, `cloud orders = gateway orders (${gwOrders.length})`);
    assertEqual(Number(pg.psql('SELECT COUNT(*) FROM sync_events')), outboxCount, `cloud events = gateway outbox (${outboxCount})`);
    assertEqual(Number(pg.psql('SELECT COUNT(DISTINCT event_id) FROM sync_events')), outboxCount, 'no duplicated event');
    assertEqual(gwValue('SELECT COUNT(*) FROM sync_outbox WHERE acked_at IS NULL'), 0, 'nothing left unacknowledged');

    const key = cloudCli('report-key', '--org', orgId, '--business', businessId, '--branch', branchId, '--name', 'e2e').report_key;
    const report = await httpsGetJson(`https://127.0.0.1:${cloudPort}/v1/reports/branches/${branchId}/orders`, ca, { Authorization: `Bearer ${key}` });
    assertEqual(report.status, 200, 'branch report readable with a branch-scoped key');
    assertEqual(
      report.body.map((r) => `${r.order.number}:${r.order.status}:v${r.order.version}`),
      gwOrders.map((o) => `${o.number}:${o.status}:v${o.version}`),
      'cloud report = local orders (number, status, version)',
    );
    for (const order of [...offline]) {
      const line = report.body.find((r) => r.order.id === order.id);
      assertEqual(line?.order.totalMinor, order.totalMinor, `offline order n°${order.number}: total ${order.totalMinor} in the cloud`);
    }
    const anonymous = await httpsGetJson(`https://127.0.0.1:${cloudPort}/v1/reports/branches/${branchId}/orders`, ca);
    assertEqual(anonymous.status, 401, 'reports refuse anonymous access');

    // Branch authority: the local orders were never modified by the sync.
    assertEqual(gw('SELECT * FROM shared_orders ORDER BY number'), beforeRecovery, 'local orders byte-identical before and after recovery sync');
    assertEqual(gw('SELECT * FROM shared_order_lines ORDER BY order_id, product_id'), linesBefore, 'local order lines unchanged by synchronization');
    assertEqual(gwValue('PRAGMA integrity_check'), 'ok', 'gateway SQLite intact after the hard kill');

    await session.stop(pos, { force: false });
    const errors = session.logEntries().filter((e) => e.level === 'Error' || e.level === 'Critical');
    assertEqual(errors.map((e) => e.message), [], 'POS logs: no errors');
  } finally {
    for (const child of [cloud, gateway]) {
      try {
        if (child && child.exitCode === null) execFileSync('taskkill', ['/F', '/PID', String(child.pid)], { stdio: 'ignore' });
      } catch {
        /* already gone */
      }
    }
    try {
      pg.stop();
    } catch (e) {
      console.warn(`[e2e] could not stop PostgreSQL: ${e.message}`);
    }
  }
});
