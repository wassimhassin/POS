// End-to-end: print queue with an unavailable printer, through the real app and the real Windows spooler.
//
// No physical printer is needed: every destination points at a printer name that does not exist, so
// OpenPrinter fails inside winspool. Verifies that jobs stay queued with the error, that a second
// send requires duplicate confirmation, and that nothing is lost across an application restart.
//
// Usage: node tests/e2e/print-queue.mjs [path\to\Debug\Pos.Desktop.exe]

import { assert, assertEqual, desktopExe, log, randomPort, runTest, sleep, waitUntil } from './lib.mjs';

const exe = desktopExe('Debug', process.argv[2]);
const port = randomPort();
const MISSING_PRINTER = 'POS-E2E-Unplugged-Printer';

/** Calls the bridge from the page with a private listener (same allowlisted protocol as the UI). */
const bridge = (cdp, type, payload) =>
  cdp.evaluate(`new Promise((resolve) => {
    const id = crypto.randomUUID();
    const handler = (e) => {
      if (e.data && e.data.id === id) { window.chrome.webview.removeEventListener('message', handler); resolve(e.data); }
    };
    window.chrome.webview.addEventListener('message', handler);
    window.chrome.webview.postMessage({ v: 1, id, type: ${JSON.stringify(type)}, payload: ${JSON.stringify(payload)} });
  })`);

const click = (cdp, testId) => cdp.evaluate(`document.querySelector('[data-testid="${testId}"]').click()`);
const exists = (cdp, testId) => cdp.evaluate(`!!document.querySelector('[data-testid="${testId}"]')`);

async function jobs(cdp) {
  const res = await bridge(cdp, 'print.jobs.list', {});
  if (!res.ok) throw new Error(res.error.message);
  return res.result;
}

await runTest('print-queue', async (session) => {
  log(`exe: ${exe}`);
  let app = await session.launch(exe, port);

  for (const id of ['receipt', 'kitchen', 'bar', 'counter']) {
    const res = await bridge(app.cdp, 'print.destinations.update', { id, printerName: MISSING_PRINTER, paperWidthMm: id === 'bar' ? 58 : 80 });
    assert(res.ok, `destination ${id} → missing printer`);
  }

  await app.cdp.evaluate(`document.querySelector('[data-product-id="p-espresso"]').click()`);
  await app.cdp.evaluate(`document.querySelector('[data-product-id="p-pizza"]').click()`);
  await waitUntil(() => app.cdp.evaluate(`!document.querySelector('[data-testid="print-tickets"]').disabled`), 'print enabled after save');

  await click(app.cdp, 'print-tickets');
  await waitUntil(async () => (await jobs(app.cdp)).length === 2, 'two station jobs');
  const attempted = await waitUntil(async () => {
    const list = await jobs(app.cdp);
    return list.every((j) => j.attempts >= 1 && j.status === 'queued') ? list : null;
  }, 'first attempt on unavailable printer');
  assertEqual(attempted.map((j) => j.destinationId).sort(), ['bar', 'kitchen'], 'routed to bar + kitchen');
  assert(attempted.every((j) => j.lastError.includes('introuvable')), 'spooler error recorded', JSON.stringify(attempted.map((j) => j.lastError)));
  assert(attempted.every((j) => j.nextAttemptAt), 'automatic retry scheduled');

  // Second send → duplicate warning. Cancel adds nothing; confirm adds marked copies.
  await click(app.cdp, 'print-tickets');
  await waitUntil(() => exists(app.cdp, 'confirm-dialog'), 'duplicate warning dialog');
  await app.cdp.evaluate(`[...document.querySelectorAll('[data-testid="confirm-dialog"] button')].find((b) => b.textContent === 'Annuler').click()`);
  await sleep(300);
  assertEqual((await jobs(app.cdp)).length, 2, 'cancel adds no job');
  await click(app.cdp, 'print-tickets');
  await waitUntil(() => exists(app.cdp, 'confirm-dialog'), 'duplicate warning dialog (again)');
  await click(app.cdp, 'confirm-accept');
  await waitUntil(async () => (await jobs(app.cdp)).length === 4, 'confirmed duplicates enqueued');
  assertEqual((await jobs(app.cdp)).map((j) => j.copyNumber).sort(), [0, 0, 1, 1], 'copies are numbered');

  // The print panel lists the jobs with the error.
  await click(app.cdp, 'open-print-panel');
  await waitUntil(() => app.cdp.evaluate(`document.querySelectorAll('[data-testid="print-job"]').length === 4`), 'panel lists 4 jobs');
  const before = await jobs(app.cdp);
  await session.stop(app, { force: false });

  // Restart: every job is still there, none lost, none marked sent.
  app = await session.launch(exe, port);
  const after = await jobs(app.cdp);
  assertEqual(after.map((j) => j.id).sort(), before.map((j) => j.id).sort(), 'all jobs survive restart');
  assert(after.every((j) => j.status === 'queued' || j.status === 'failed'), 'no job claims to be sent', JSON.stringify(after.map((j) => j.status)));
  assert(
    after.every((a) => a.attempts >= before.find((b) => b.id === a.id).attempts),
    'retry counters preserved',
  );
  await session.stop(app, { force: false });

  // Structured logs describe each attempt (copies wait FIFO behind the originals' retry delay).
  const attemptLogs = session.logEntries().filter((e) => e.category === 'Pos.Core.Printing.PrintQueue' && e.props?.Outcome);
  const recordedAttempts = after.reduce((sum, j) => sum + j.attempts, 0);
  assert(
    attemptLogs.length >= recordedAttempts && recordedAttempts >= 2,
    `every recorded attempt is logged (${attemptLogs.length} logs, ${recordedAttempts} attempts)`,
  );
  assert(attemptLogs.every((e) => e.props.Outcome === 'NotSent' && e.props.PrinterName === MISSING_PRINTER), 'logs carry outcome + printer');
  const errors = session.logEntries().filter((e) => e.level === 'Error' || e.level === 'Critical');
  assertEqual(errors.map((e) => e.message), [], 'log: no errors');
});
