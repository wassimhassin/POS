// Security regression test: a Release build must NOT expose the WebView2 DevTools protocol, even
// when WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS asks for --remote-debugging-port.
//
// Positive control: restart-persistence.mjs uses the very same variable against the Debug build
// and depends on the port opening, so this test cannot pass merely because the variable is ignored.
//
// Usage: node tests/e2e/release-no-remote-debugging.mjs [path\to\Release\Pos.Desktop.exe]

import { assert, assertEqual, desktopExe, isPortOpen, log, randomPort, runTest, sleep, waitUntil } from './lib.mjs';

const exe = desktopExe('Release', process.argv[2]);
const port = randomPort();

await runTest('release-no-remote-debugging', async (session) => {
  log(`exe: ${exe}`);
  const app = session.start(exe, { port });

  // The UI finished loading → the WebView2 browser process is fully up.
  await waitUntil(() => session.logEntries().some((e) => e.message.startsWith('UI loaded')), 'UI loaded log entry');
  await sleep(1500); // give a (hypothetical) DevTools listener time to bind

  assert(!(await isPortOpen(port)), `remote debugging port ${port} must be closed`);
  assertEqual(session.findFiles('DevToolsActivePort'), [], 'no DevToolsActivePort file in user data');
  assert(
    session
      .logEntries()
      .some((e) => e.message === 'Removed WebView2 environment override WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'),
    'override removal was logged',
  );

  app.cdp = null;
  await session.stop(app, { force: false });
});
