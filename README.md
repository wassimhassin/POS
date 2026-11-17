# SaaS POS — Phase 1 technical prototype

A minimal Windows proof of concept for the future multi-tenant café-restaurant POS. It shows that a
**.NET 10 WPF + WebView2** shell can host a **React/TypeScript/Vite/Tailwind** touchscreen UI, talk to it
through a narrow, validated bridge, and persist data in **SQLite** across restarts.

> This is a feasibility spike, not the production POS. No cloud backend, authentication, tenants,
> subscriptions, payments or printing. See `docs/` for the full specifications.

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
tests/e2e/                   end-to-end restart test driving the real app
scripts/                     build.ps1, run.ps1, test.ps1
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

Allowlisted commands: `products.list`, `draft.load`, `draft.save`, `draft.clear`, `log.client`.

Security measures:
- Messages are accepted only from the trusted UI origin. Navigation to any other origin, popups and new windows are blocked.
- No host objects are exposed. DevTools, context menus and browser shortcuts are enabled only in Debug builds.
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
