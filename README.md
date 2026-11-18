# SaaS POS — Phase 1 / 1.2 / 1.3 technical prototype

A minimal Windows proof of concept for the future multi-tenant café-restaurant POS. It shows that a
**.NET 10 WPF + WebView2** shell can host a **React/TypeScript/Vite/Tailwind** touchscreen UI, talk to it
through a narrow, validated bridge, and persist data in **SQLite** across restarts.

> This is a feasibility spike, not the production POS. No cloud backend, authentication, tenants,
> subscriptions, payments or cloud sync. Phase 1.2 adds a thermal-printing proof of concept (see
> [Printing](#printing-phase-12)); Phase 1.3 adds a LAN branch gateway for shared orders between tills
> (see [LAN gateway](#lan-gateway-phase-13)). See `docs/` for the full specifications.

## Layout

```text
PosSaaS.slnx                 .NET solution
global.json                  pins the .NET SDK (10.0.404, roll forward to latest feature band)
apps/
  pos-web/                   React + TypeScript + Vite + Tailwind UI
    src/bridge/              typed bridge client + contracts (mirror of Pos.Core/Bridge)
    src/cart/                pure cart logic + draft persistence hook
    src/components/          ProductCard, CartPanel, SaveStatusBadge, ErrorBoundary
  windows-pos/
    Pos.Desktop/             thin WPF host: window, WebView2, bridge transport, composition root
    Pos.Core/                UI-independent core: SQLite, repositories, bridge dispatcher, JSON logger
    Pos.Core.Tests/          xUnit tests (database, persistence across restart, bridge security)
  branch-gateway/
    Pos.Gateway/             LAN gateway: ASP.NET Core minimal API + SignalR, SQLite, CLI / Windows Service
    Pos.Gateway.Tests/       integration tests: real gateway process + 2 simulated terminals
tests/e2e/                   end-to-end tests driving the real app (restart, printing, release, lan-gateway)
scripts/                     build, run, test, run-gateway, install-gateway-service
docs/                        PRD, architecture, database, UI/UX, roadmap
```

## Prerequisites

- Windows 10/11 x64
- .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`)
- Node.js 22+ (tested with 24) and npm
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)

## Commands

From the repository root, in PowerShell:

| Task | Command |
|---|---|
| Build UI + .NET | `.\scripts\build.ps1` (add `-Configuration Release` for release) |
| Build and run | `.\scripts\run.ps1` (or `-NoBuild` to skip the build) |
| Run with Vite hot reload (Debug only) | `npm run dev --prefix apps/pos-web`, then `.\scripts\run.ps1 -UiUrl http://localhost:5173` |
| All tests | `.\scripts\test.ps1` (add `-SkipE2E` for unit tests only) |

If script execution is blocked, prefix with `powershell -ExecutionPolicy Bypass -File`.

Individual steps:

```powershell
npm ci --prefix apps/pos-web; npm run build --prefix apps/pos-web   # UI → apps/pos-web/dist
dotnet build PosSaaS.slnx                                          # copies dist → Pos.Desktop/bin/.../wwwroot
npm test --prefix apps/pos-web                                     # Vitest (cart, money, bridge parsing)
dotnet test PosSaaS.slnx                                           # xUnit
node tests/e2e/restart-persistence.mjs                             # real app, 4 launches
node tests/e2e/print-queue.mjs                                     # real app + real spooler, missing printer, restart
node tests/e2e/release-no-remote-debugging.mjs                     # Release build ignores WebView2 debug overrides
```

Optional hardware test (prints one real sample receipt; a person must check the paper):

```powershell
$env:POS_TEST_PRINTER = "Nom exact de l'imprimante"; $env:POS_TEST_PAPER_MM = "80"
dotnet test PosSaaS.slnx --filter Hardware_sample_receipt
```

## How it works

### Hosting
The React build is copied next to the executable (`wwwroot`) and served by WebView2 from the virtual
host `https://pos-app.example` (`SetVirtualHostNameToFolderMapping`). No dev server or local HTTP
listener is needed at runtime. The packaged `index.html` carries a strict Content-Security-Policy
(`script-src 'self'`, no remote connections).

### Bridge (React ↔ C#)
Request/response over `chrome.webview.postMessage` / `PostWebMessageAsJson`:

```text
UI   → host  { v: 1, id: "<uuid>", type: "draft.save", payload: { orderId, lines: [{ productId, quantity }] } }
host → UI    { id, ok: true, result }   |   { id, ok: false, error: { code, message } }
```

Allowlisted commands: `products.list`, `draft.load`, `draft.save`, `draft.clear`, `log.client`, and for
printing `printers.list`, `print.destinations.list`, `print.destinations.update`, `routing.update`,
`print.receipt`, `print.tickets`, `print.jobs.list`, `print.jobs.preview`, `print.jobs.retry`,
`print.jobs.reprint`.

Security measures:
- Messages are accepted only from the trusted UI origin. Navigation to any other origin, popups and new windows are blocked.
- No host objects are exposed. DevTools, context menus and browser shortcuts are enabled only in Debug builds.
- **Remote debugging hardening (Release).** WebView2 also honours the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`
  environment variable (e.g. `--remote-debugging-port=9222`, which would let any local process drive the UI and
  call the bridge) and the other `WEBVIEW2_*` overrides. Release builds remove every `WEBVIEW2_*` variable from
  the process before WebView2 starts, and log each removal. The machine/user policy
  `Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments` cannot be overridden by the app, so
  Release refuses to start if it contains a debugging switch. Regression test:
  `tests/e2e/release-no-remote-debugging.mjs`. Debug builds deliberately keep these overrides (the E2E tests
  use them): **never ship a Debug build.**
- Strict validation: 64 KB size limit, depth limit, unknown envelope or payload fields rejected, strict numeric types, request IDs matched against a pattern.
- **The UI never sends prices.** C# reads names and prices from SQLite inside the write transaction, and stores them as snapshots on the draft lines.
- Unexpected exceptions are logged in full but return a generic `internal_error` to the UI.

The contract lives in `apps/pos-web/src/bridge/contracts.ts` and `Pos.Core/Bridge/BridgeDispatcher.cs`.
Keep both in sync.

### Persistence
- SQLite file: `%LOCALAPPDATA%\PosSaaS\pos.db`. Set the `POS_DATA_DIR` environment variable to override it.
- WAL journal mode, `synchronous=FULL`, foreign keys on, and `quick_check` at startup. Migrations are versioned with `PRAGMA user_version`; the app refuses a database written by a newer schema version.
- Tables: `products` (12 sample café items, seeded on first run), `draft_orders`, `draft_order_lines`.
- Money is stored as integer minor units (TND millimes, 3 decimals). There is no floating-point arithmetic anywhere.
- The UI saves every cart change through a serialized, coalescing queue. The header shows *Enregistrement…* while saving, *Enregistré localement* only after SQLite commits, and *Échec* with a retry button if a save fails.

### Logging and errors
- Structured JSON Lines at `%LOCALAPPDATA%\PosSaaS\logs\pos-YYYYMMDD.jsonl`, one object per event with `ts`, `level`, `category`, `message`, `props` and `exception`.
- Bridge requests are logged with command, request ID and timing. React errors (from the error boundary, `window.onerror` and unhandled rejections) are forwarded through `log.client`.
- Native fallbacks: a missing WebView2 runtime or a missing UI build shows a clear French message. A crashed render process is reloaded, and the draft comes back from SQLite.

## Printing (Phase 1.2)

A proof of concept, **not** the production printing system. Code: `Pos.Core/Printing/`, UI: `apps/pos-web/src/printing/`.

### Flow

```text
UI "Ticket client" / "Bons de préparation"
  → PrintService: reads the SAVED draft from SQLite (UI sends only the orderId), routes lines,
    snapshots the ticket source + paper width, inserts print_jobs rows in ONE transaction
  → PrintWorker (background, FIFO per destination): renders ESC/POS (CP858) → WindowsSpoolerPrinter
    (winspool: OpenPrinter / StartDocPrinter "RAW" / WritePrinter / EndDocPrinter)
  → result recorded in print_jobs + print_job_events + JSON log
```

- **Destinations** (`print_destinations`): `receipt` (Ticket client) and preparation stations
  (`kitchen` Cuisine, `bar` Bar, `counter` Comptoir). Each has a Windows printer name and a 58 or 80 mm
  width (32 / 48 columns), editable in the *Impression → Imprimantes* panel (printers discovered with
  `EnumPrinters`).
- **Routing**: `products.station_id`, editable in *Impression → Routage*. Items with no station produce no
  preparation ticket and are reported to the cashier as "non routés".
- **Money**: integer millimes end to end; formatting `1 234,500` only at render time.
- Kitchen/bar tickets print item names and quantities in double size, without prices.

### Job states — what each one actually means

| State | Meaning | Automatic action | Operator action |
|---|---|---|---|
| `queued` | Not handed to Windows yet (new, or last attempt **definitely** failed before sending) | Retried with backoff 5/15/30/60 s | *Réessayer* (now) |
| `printing` | An attempt is in progress | — | — |
| `sent` | **The Windows spooler accepted the job.** This is NOT proof that paper came out | — | *Réimprimer…* (duplicate warning) |
| `failed` | 5 automatic attempts failed, nothing sent | — | *Réessayer* (safe) |
| `uncertain` | Failure *after* data started flowing, an exception in the driver, or the app died mid-attempt (`printing` at startup) — the ticket **may or may not** have printed | **Never retried automatically** | Check the printer, then *Réimprimer…* |

Rules:
- **No exactly-once guarantee.** The system only distinguishes "certainly not sent" (safe to retry) from
  "sent or maybe sent" (re-printing may duplicate). Every job that may duplicate paper requires an
  explicit confirmation, and copies are printed with a `DUPLICATA n°X` banner (receipt) or
  `REIMPRESSION n°X — Ne pas preparer en double` (stations).
- Sending the receipt/tickets of the same order twice requires confirmation (`duplicate_confirmation_required`).
- Jobs are rows in SQLite: an unavailable printer, closing the app or a crash never loses them. The
  printer is resolved at each attempt, so fixing the configuration un-blocks waiting jobs.
- The UI header shows *Impression — N à vérifier* for `failed` + `uncertain` jobs.

### Verification: simulated vs. real

| What | How | Status |
|---|---|---|
| Layout 58/80 mm, wrapping, millimes, duplicate banners, CP858 bytes, ESC/POS init/cut | Unit tests (exact text snapshot) | ✅ automated |
| Routing, duplicate confirmation, persistence, restart, FIFO, retry limits, uncertain outcomes, crash recovery, migration v1 → v2 | Unit tests with a **simulated** printer (`FakePrinterGateway`) | ✅ automated, **simulated** |
| Printer discovery, missing printer → `NotSent` | Real Windows spooler, no hardware | ✅ automated, real spooler |
| RAW job accepted by the spooler | Real spooler → *Microsoft Print to PDF* with an output file | ✅ accepted (`Sent`), but that driver discards RAW data: **proves nothing about paper** |
| Missing printer through the real app, duplicate dialog, restart | `tests/e2e/print-queue.mjs` | ✅ automated, real spooler |
| **Paper output on a real thermal printer** | `POS_TEST_PRINTER` hardware test + human check | ❌ **not done — no thermal printer available** |

### Known limitations
- `sent` only means the spooler accepted the job. Most USB thermal drivers report "ready" even when
  unplugged, out of paper or with the cover open; jobs then sit in the Windows queue.
- The code page command (`ESC t 19`, CP858) and the cut command (`GS V 66 0`) are common ESC/POS but vary by
  model; unsupported characters print as `?`.
- Drivers that reject the `RAW` datatype (many v4/"class" drivers) fail with `NotSent`; ESC/POS printers need
  the vendor driver or *Generic / Text Only*.
- Avoid targeting *Microsoft Print to PDF* from the app: without an output file it opens a save dialog.
- No print-job cleanup/retention, no status read-back (ASB), no cash drawer, no logo/QR, no LAN printers
  without a Windows queue. No cloud sync, no LAN gateway (out of scope).

### Hardware tests still to run
1. Real 58 mm and 80 mm ESC/POS printers (USB and network-port queues): layout, accents (é è à ç €), cut.
2. Printer off / unplugged / out of paper / cover open before sending: observe what the spooler and our state report.
3. Unplug or power off **during** a job, and kill the app during a job: confirm `uncertain` handling and duplicate banners.
4. Jobs stuck in the Windows queue while the printer is offline, then printed later (possible late duplicates after a reprint).
5. Several stations on different printers at once; throughput during a rush.
6. Driver variants: vendor driver vs. *Generic / Text Only*, v3 vs. v4.

## LAN gateway (Phase 1.3)

Two or more tills share restaurant orders through one **branch gateway** on the LAN, with no internet.
The full design, security model, failure handling, single-point-of-failure recovery procedure, remaining
risks and multi-machine tests to run are in [docs/06-LAN-GATEWAY-PROTOTYPE.md](docs/06-LAN-GATEWAY-PROTOTYPE.md).

Try it on one PC:

```powershell
.\scripts\build.ps1
.\scripts\run-gateway.ps1          # console gateway on https://0.0.0.0:7443 (initializes a dev branch)
# In a second terminal: one pairing code per till (prints the code + certificate fingerprint)
apps\branch-gateway\Pos.Gateway\bin\Debug\net10.0-windows\Pos.Gateway.exe enroll --name "Caisse A"
$env:POS_DATA_DIR = "$env:TEMP\pos-a"; .\scripts\run.ps1 -NoBuild   # repeat with pos-b for a second till
```

In the POS, click the gateway badge (top right) and enter `https://127.0.0.1:7443`, the fingerprint and
the code, then use **Commandes partagées**. Gateway CLI: `init`, `run`, `enroll`, `terminals`, `revoke`,
`printer`, `backup`, `status` (all take `--data-dir`). Deployment as a Windows Service:
`scripts/install-gateway-service.ps1` (elevated; not executed during this validation).

Key properties: HTTPS only with certificate pinning, per-terminal key pairs (no shared secret), one-time
pairing codes, the gateway as the only SQLite writer, idempotent operations, optimistic versioning for
overwrites, one durable print intent per station on submission, fail-closed while the gateway is down,
and **no automatic failover** (the gateway is a documented single point of failure).
