// End-to-end check that the draft order survives application restarts.
//
// Launches the real WPF app (isolated data dir via POS_DATA_DIR), drives the React UI through the
// WebView2 DevTools protocol, then closes/kills and relaunches the app and checks the cart was
// restored from SQLite. Requires a Debug build: Release builds strip WEBVIEW2_* variables, so
// remote debugging cannot be enabled there (see release-no-remote-debugging.mjs).
//
// Usage: node tests/e2e/restart-persistence.mjs [path\to\Debug\Pos.Desktop.exe]

import { assertEqual, desktopExe, log, randomPort, runTest, sleep, waitUntil } from './lib.mjs';

const exe = desktopExe('Debug', process.argv[2]);
const port = randomPort();

const cartState = (cdp) =>
  cdp.evaluate(`[...document.querySelectorAll('[data-testid="cart-line"]')]
      .map((e) => [e.dataset.productId, Number(e.dataset.quantity)])`);

const total = (cdp) => cdp.evaluate(`document.querySelector('[data-testid="cart-total"]').textContent.replace(/\\s/g, ' ')`);

const tap = (cdp, productId) =>
  cdp.evaluate(`document.querySelector('[data-testid="product-card"][data-product-id="${productId}"]').click()`);

async function waitSaved(cdp) {
  await sleep(400); // let the save start before polling for its completion
  await waitUntil(() => cdp.evaluate(`document.querySelector('[data-save-status]').dataset.saveStatus === 'saved'`), 'save');
}

await runTest('restart-persistence', async (session) => {
  log(`exe: ${exe}`);

  // Run 1: fresh database, build a cart, close normally.
  let app = await session.launch(exe, port);
  assertEqual(await cartState(app.cdp), [], 'run 1: cart starts empty');
  await tap(app.cdp, 'p-espresso');
  await tap(app.cdp, 'p-espresso');
  await tap(app.cdp, 'p-croissant');
  await waitSaved(app.cdp);
  const expected1 = [['p-espresso', 2], ['p-croissant', 1]];
  assertEqual(await cartState(app.cdp), expected1, 'run 1: cart after taps');
  assertEqual(await total(app.cdp), '7,000 DT', 'run 1: total');
  await session.stop(app, { force: false });

  // Run 2: restored after a normal restart; add an item, then hard-kill the process.
  app = await session.launch(exe, port);
  assertEqual(await cartState(app.cdp), expected1, 'run 2: cart restored after restart');
  await tap(app.cdp, 'p-pizza');
  await waitSaved(app.cdp);
  await session.stop(app, { force: true });

  // Run 3: acknowledged save survived the forced kill; clear the order.
  app = await session.launch(exe, port);
  const expected3 = [...expected1, ['p-pizza', 1]];
  assertEqual(await cartState(app.cdp), expected3, 'run 3: cart restored after forced kill');
  assertEqual(await total(app.cdp), '21,000 DT', 'run 3: total');
  await app.cdp.evaluate(`document.querySelector('[data-testid="clear-cart"]').click()`);
  await waitSaved(app.cdp);
  await session.stop(app, { force: false });

  // Run 4: cleared order stays cleared.
  app = await session.launch(exe, port);
  assertEqual(await cartState(app.cdp), [], 'run 4: cleared cart stays empty');
  await session.stop(app, { force: false });

  // Structured logs were written and contain no errors.
  const entries = session.logEntries();
  assertEqual(entries.filter((e) => e.message.startsWith('Application starting')).length, 4, 'log: 4 startups recorded');
  const errors = entries.filter((e) => e.level === 'Error' || e.level === 'Critical');
  assertEqual(errors.map((e) => e.message), [], 'log: no errors');
});
