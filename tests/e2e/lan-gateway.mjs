// End-to-end: two real POS desktop instances coordinate a shared order through one real gateway
// process (HTTPS + SignalR on 127.0.0.1), including a hard gateway crash and restart.
//
//   A creates an order → B sees it → both modify it (concurrent adds kept, conflicting overwrite
//   rejected) → A submits → duplicate submissions create nothing → gateway killed and restarted →
//   both terminals recover the same state; B restarts and keeps its identity.
//
// Usage: node tests/e2e/lan-gateway.mjs

import { spawn } from 'node:child_process';
import path from 'node:path';
// node:sqlite is still flagged experimental in Node 24: keep its warning out of the test output.
process.removeAllListeners('warning');
const { DatabaseSync } = await import('node:sqlite');
import {
  assert,
  assertEqual,
  bridge,
  desktopExe,
  fill,
  gatewayCli,
  gatewayExe,
  log,
  randomPort,
  runTest,
  sleep,
  TestSession,
  waitForTcp,
  waitUntil,
} from './lib.mjs';

const posExe = desktopExe('Debug', process.argv[2]);
const gwExe = gatewayExe('Debug');
const gwPort = 7600 + Math.floor(Math.random() * 300);
const portA = randomPort();
const portB = portA + 1;

const $ = (cdp, js) => cdp.evaluate(js);
const click = (cdp, selector) => $(cdp, `document.querySelector(${JSON.stringify(selector)}).click()`);
const state = (cdp) => $(cdp, `document.querySelector('[data-testid="gateway-status"]').dataset.state`);

async function ok(cdp, type, payload) {
  const res = await bridge(cdp, type, payload);
  if (!res.ok) throw new Error(`${type}: ${res.error.code} ${res.error.message}`);
  return res.result;
}

const sharedOrders = async (cdp) => (await ok(cdp, 'gateway.state', {})).orders;

await runTest('lan-gateway', async (sessionA) => {
  const sessionB = new TestSession();
  const gwDir = path.join(sessionA.dataDir, 'gateway');
  let gateway;
  const startGateway = async () => {
    gateway = spawn(gwExe, ['run', '--data-dir', gwDir, '--listen', `https://127.0.0.1:${gwPort}`], { stdio: 'ignore' });
    sessionA.spawned.push(gateway);
    await waitForTcp(gwPort, 'gateway listening');
    log(`Gateway started (pid ${gateway.pid}, port ${gwPort})`);
  };
  const db = () => new DatabaseSync(path.join(gwDir, 'gateway.db'), { readOnly: true });
  const count = (sql, ...params) => {
    const conn = db();
    try {
      return Object.values(conn.prepare(sql).get(...params))[0];
    } finally {
      conn.close();
    }
  };

  try {
    // --- Gateway provisioning (administrator, on the gateway PC) ---
    gatewayCli(gwExe, gwDir, 'init', '--branch-id', crypto.randomUUID(), '--branch-name', 'Succursale E2E');
    await startGateway();
    const pairing = (name) => {
      const out = gatewayCli(gwExe, gwDir, 'enroll', '--name', name);
      return { code: /Code d'appairage : (\S+)/.exec(out)[1], fingerprint: /Empreinte\s+: (\S+)/.exec(out)[1] };
    };

    // --- Two POS terminals, each paired through the UI ---
    let a = await sessionA.launch(posExe, portA);
    let b = await sessionB.launch(posExe, portB);
    for (const [term, name] of [
      [a, 'Caisse A'],
      [b, 'Caisse B'],
    ]) {
      const { code, fingerprint } = pairing(name);
      await click(term.cdp, '[data-testid="gateway-status"]');
      await fill(term.cdp, 'gw-url', `https://127.0.0.1:${gwPort}`);
      await fill(term.cdp, 'gw-fingerprint', fingerprint);
      await fill(term.cdp, 'gw-code', code);
      await click(term.cdp, '[data-testid="gw-enroll"]');
      await waitUntil(async () => (await state(term.cdp)) === 'connected', `${name} connected`);
      log(`${name} paired and connected`);
    }
    const terminalB = (await ok(b.cdp, 'gateway.state', {})).status.terminalId;
    assertEqual(count('SELECT COUNT(*) FROM terminals'), 2, 'two enrolled terminals');

    // --- 1. Terminal A creates an order ---
    await click(a.cdp, '[data-testid="mode-shared"]');
    await click(a.cdp, '[data-testid="shared-new"]');
    const orderId = await waitUntil(() => $(a.cdp, `document.querySelector('[data-testid="shared-order"]')?.dataset.orderId`), 'A: order created');
    await click(a.cdp, '[data-product-id="p-espresso"]');
    await waitUntil(() => $(a.cdp, `!!document.querySelector('[data-testid="shared-line"][data-product-id="p-espresso"]')`), 'A: espresso added');

    // --- 2. Terminal B sees it ---
    await click(b.cdp, '[data-testid="mode-shared"]');
    await waitUntil(() => $(b.cdp, `!!document.querySelector('[data-testid="shared-order-row"][data-order-id="${orderId}"]')`), 'B: sees the new order');
    await click(b.cdp, `[data-testid="shared-order-row"][data-order-id="${orderId}"]`);
    await waitUntil(() => $(b.cdp, `document.querySelector('[data-testid="shared-line"][data-product-id="p-espresso"]')?.dataset.quantity === '1'`), 'B: sees A\'s item');

    // --- 3. Both modify it safely ---
    // Concurrent additions from both tills are both kept.
    await Promise.all([click(a.cdp, '[data-product-id="p-pizza"]'), click(b.cdp, '[data-product-id="p-croissant"]')]);
    for (const [term, name] of [
      [a, 'A'],
      [b, 'B'],
    ]) {
      await waitUntil(async () => (await sharedOrders(term.cdp)).find((o) => o.id === orderId)?.version === 4, `${name}: both additions visible (v4)`);
    }

    // Conflicting overwrite of the same line from the same version: one wins, the other is told.
    const [ra, rb] = await Promise.all([
      bridge(a.cdp, 'shared.setQuantity', { operationId: crypto.randomUUID(), orderId, productId: 'p-espresso', quantity: 3, expectedVersion: 4 }),
      bridge(b.cdp, 'shared.setQuantity', { operationId: crypto.randomUUID(), orderId, productId: 'p-espresso', quantity: 5, expectedVersion: 4 }),
    ]);
    assertEqual([ra.ok, rb.ok].filter(Boolean).length, 1, 'exactly one overwrite accepted');
    const loser = ra.ok ? rb : ra;
    assertEqual(loser.error.code, 'version_conflict', 'the other is rejected with version_conflict');
    const winnerQty = ra.ok ? 3 : 5;
    for (const [term, name] of [
      [a, 'A'],
      [b, 'B'],
    ]) {
      await waitUntil(
        () => $(term.cdp, `document.querySelector('[data-testid="shared-line"][data-product-id="p-espresso"]')?.dataset.quantity === '${winnerQty}'`),
        `${name}: shows the winning quantity ${winnerQty}`,
      );
    }

    // --- 4. Terminal A submits it ---
    await waitUntil(() => $(a.cdp, `document.querySelector('[data-testid="shared-order"]').dataset.version === '5'`), 'A: up to date before submit');
    await click(a.cdp, '[data-testid="shared-submit"]');
    await waitUntil(() => $(a.cdp, `document.querySelector('[data-testid="shared-order"]')?.dataset.status === 'submitted'`), 'A: submitted');
    await waitUntil(() => $(b.cdp, `document.querySelector('[data-testid="shared-order"]')?.dataset.status === 'submitted'`), 'B: sees submission');

    // --- 5. Duplicate submissions create nothing ---
    const submitOp = db().prepare(`SELECT operation_id FROM operations WHERE order_id = ? AND type = 'submit'`).get(orderId).operation_id;
    const replay = await ok(a.cdp, 'shared.submit', { operationId: submitOp, orderId, expectedVersion: 5 });
    assert(replay.replayed, 'same submission replayed: original result returned');
    const again = await bridge(b.cdp, 'shared.submit', { operationId: crypto.randomUUID(), orderId, expectedVersion: 6 });
    assertEqual(again.error?.code, 'order_already_submitted', 'second submission refused');
    assertEqual(count('SELECT COUNT(*) FROM shared_orders'), 1, 'one order');
    assertEqual(count(`SELECT COUNT(*) FROM operations WHERE order_id = ? AND type = 'submit'`, orderId), 1, 'one submit operation');
    assertEqual(count('SELECT COUNT(*) FROM print_intents WHERE order_id = ?', orderId), 3, 'one print intent per station (bar, kitchen, counter)');
    assertEqual(count('SELECT COUNT(*) FROM print_jobs WHERE order_id = ?', orderId), 3, 'one durable print job per intent');

    // --- 6. Gateway crashes and restarts ---
    const before = (await sharedOrders(a.cdp)).find((o) => o.id === orderId);
    gateway.kill();
    for (const [term, name] of [
      [a, 'A'],
      [b, 'B'],
    ]) {
      await waitUntil(async () => (await state(term.cdp)) === 'offline', `${name}: shows gateway offline`);
      await waitUntil(() => $(term.cdp, `!!document.querySelector('[data-testid="shared-blocked"]')`), `${name}: shared changes blocked`);
    }
    const blocked = await bridge(b.cdp, 'shared.create', { operationId: crypto.randomUUID(), label: null });
    assertEqual(blocked.error?.code, 'gateway_unavailable', 'writes refused while the gateway is down (fail closed)');

    await sleep(1000);
    await startGateway();
    for (const [term, name] of [
      [a, 'A'],
      [b, 'B'],
    ]) {
      await waitUntil(async () => (await state(term.cdp)) === 'connected', `${name}: reconnected`, 45_000);
      const recovered = (await sharedOrders(term.cdp)).find((o) => o.id === orderId);
      assert(JSON.stringify(recovered) === JSON.stringify(before), `${name}: recovered the exact shared state (v${recovered?.version}, ${recovered?.status}, ${recovered?.itemCount} items)`);
    }

    // Live sync works again after the restart.
    const created = await ok(b.cdp, 'shared.create', { operationId: crypto.randomUUID(), label: 'Après redémarrage' });
    await waitUntil(async () => (await sharedOrders(a.cdp)).some((o) => o.id === created.order.id), 'A: sees an order created after restart');

    // --- 7. Terminal B restarts: same identity, no new pairing ---
    await sessionB.stop(b, { force: false });
    b = await sessionB.launch(posExe, portB);
    await waitUntil(async () => (await state(b.cdp)) === 'connected', 'B: reconnected after its own restart');
    const afterRestart = await ok(b.cdp, 'gateway.state', {});
    assertEqual(afterRestart.status.terminalId, terminalB, 'B kept its terminal identity');
    assert(afterRestart.orders.some((o) => o.id === orderId && o.status === 'submitted'), 'B: still sees the submitted order');
    assertEqual(count('SELECT COUNT(*) FROM terminals'), 2, 'no extra enrollment');

    await sessionA.stop(a, { force: false });
    await sessionB.stop(b, { force: false });

    const errors = [...sessionA.logEntries(), ...sessionB.logEntries()].filter((e) => e.level === 'Error' || e.level === 'Critical');
    assertEqual(errors.map((e) => e.message), [], 'POS logs: no errors');
    const conn = db();
    assertEqual(conn.prepare('PRAGMA integrity_check').get().integrity_check, 'ok', 'gateway SQLite intact after hard kill');
    conn.close();
  } finally {
    await sessionB.cleanup();
  }
});
