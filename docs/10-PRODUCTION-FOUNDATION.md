# 10 — Production foundation (Phase 2.0)

**Status:** plan for discussion; nothing in this phase is implemented. It turns the validated Phase 1
prototypes into a module map and a prioritized roadmap for incremental production work. Validated components
are **extended, not rebuilt**. Architecture decisions are logged in [DECISIONS.md](DECISIONS.md) (ADR-001 to ADR-012).

Sources: docs 01–09 and the repository at commit `f7fead1`. Where docs 01–05 (written before the prototypes) and the code
disagree, this document records which one prevails.

---

## 1. Current state audit

### 1.1 Projects

| Project | Runtime | What it is today | Validated by |
|---|---|---|---|
| `apps/windows-pos/Pos.Desktop` | WPF + WebView2, net10.0-windows | Thin host: loads `pos-web` from a virtual host. DevTools, context menus and debug switches are off in Release. | E2E (restart, print, release hardening, LAN, cloud) |
| `apps/windows-pos/Pos.Core` | Class library | Allowlisted bridge (origin-checked), terminal SQLite (`PosDatabase`), print queue/worker/spooler, `GatewayClient` (pairing, pending operations, reconnect), JSON file logging | 135 unit/integration tests |
| `apps/pos-web` | React 19 + Vite + Tailwind v4 | Single-till cart, shared orders panel, printing panel, gateway pairing/unpair dialog | 21 Vitest + E2E |
| `apps/branch-gateway/Pos.Gateway` | ASP.NET Core, Windows Service | Branch authority: SQLite WAL, idempotent `OrderService`, device-key terminal auth, SignalR, print intents, outbox sync, backup/restore/reconcile, CLI | 36 process-level tests + reliability suite |
| `apps/cloud-api/Pos.Cloud` | ASP.NET Core + EF Core/Npgsql, net10.0 | Organization → Business → Branch rows, gateway registration/credentials, idempotent sync ingest, read models (orders, lines, catalog), scoped report keys, admin CLI | 28 process-level tests + reliability suite |
| `apps/shared/Pos.Sync.Contracts` | Class library | Sync wire contracts, canonical event hash, signing payloads | Through gateway/cloud tests |

Tooling: pinned SDK (`global.json` 10.0.404); `TreatWarningsAsErrors` with `latest-recommended` analyzers;
PowerShell build/test/run scripts; E2E in Node against real processes. There is **no CI pipeline**.

### 1.2 Data models

- **Cloud (PostgreSQL, 2 migrations):** `organizations`, `businesses`, `branches` (composite keys
  `(organization_id, business_id, id)`), `gateway_credentials` (one active per branch), `provisioning_codes`,
  `branch_sync_states`, `sync_events` (unique per branch sequence and event id), read models `orders`,
  `order_lines`, `branch_products`, plus `report_keys`. There are two database roles: a migration owner and a DML-only `pos_app`.
- **Gateway (SQLite, 3 migrations):** identity, `terminals`, `enrollment_codes`, `shared_orders`/`lines`,
  `operations` (idempotency and journal), `print_intents` + Phase 1.2 print tables, `sync_outbox`, `cloud_link`,
  `sync_state`, recovery state/quarantine, backup config/runs.
- **Terminal (SQLite):** products (seed), draft orders, print destinations/jobs/events, terminal identity,
  gateway link, pending operations.

### 1.3 APIs

| Surface | Endpoints | Auth |
|---|---|---|
| Gateway LAN | `/v1/enroll`, `/v1/auth/challenge`, `/v1/auth/token`; `/v1/branches/{id}/me`, `orders` (GET/POST), `orders/{id}` (GET), `items`, `quantity`, `submit`, `print-intents`, `operations/{id}/resolve`; SignalR hub | One-time code + terminal ECDSA key → 1 h bearer token; revocation checked per request; rate limits per IP |
| Cloud sync | `/v1/gateways/register`, `POST/GET /v1/sync/events`, `GET /v1/sync/state` | Gateway ECDSA signature over method, path, query and body (±5 min) |
| Cloud reports | `/v1/reports/organizations/{id}`, `businesses/{id}`, `branches/{id}`, `branches/{id}/orders`, `branches/{id}/catalog` | Scoped report keys (`posrk.*`); out-of-scope → 404 |
| Admin | CLI only (`org-create`, `business-create`, `branch-create`, `gateway-code`, `report-key`, …) | Machine access |

### 1.4 Reusable as-is (validated — extend, do not rewrite)

1. **Gateway command engine:** operation ids, replay/resolve, optimistic versions, single serialized writer,
   SignalR snapshot + broadcast. This pattern carries every future branch command: tables, payments, cash, KDS states.
2. **Outbox → cloud sync:** ordered and idempotent; contradictions block instead of being skipped; reconciliation and fencing. New
   event types are added to it; it is not replaced.
3. **Device trust:** terminal key pairing, gateway ECDSA credentials, one-time codes, revocation,
   one-active-gateway-per-branch.
4. **Tenant hierarchy and isolation tests** (composite keys, scope from credentials, cross-tenant tests).
5. **Backup/restore/reconcile and recovery state** (doc 08), and **print pipeline** with uncertainty (Phase 1.2).
6. **Desktop shell hardening and bridge** (allowlist, origin check, payload validation).
7. **Test kits:** real-process test kits, the private PostgreSQL cluster, the fault relay and the opt-in reliability suite.

### 1.5 Prototype-only (to be replaced as the matching module lands)

| Item | Replaced by |
|---|---|
| `SampleData` seed catalog with string ids (`p-espresso`) in terminal and gateway | Cloud catalog → gateway configuration snapshots (ADR-004) |
| Gateway as catalog authority, cloud catalog as a read copy | Cloud authoritative for configuration (ADR-004) |
| Report keys as the only way to read the cloud | User auth for the dashboard; report keys kept for integrations only |
| Admin CLI as the only management surface | Management API + dashboard (the CLI stays for operations and bootstrap) |
| Anonymous use of a paired POS | Staff sign-in and permissions on the gateway (ADR-006) |
| "Vente directe" local single-till draft | Folded into the gateway order model (decide at 2.4; it must not become a second writer) |
| Dev certificates + SHA-256 pinning for the cloud | Public CA certificate for the cloud; pinning kept on the LAN only |

### 1.6 Missing production capabilities

- **Identity & access:** users, sign-in, MFA, sessions, roles/permissions, branch-scoped authorization, platform
  admin separation, and an audit log.
- **Management:** organization/business/branch settings (legal, fiscal, timezone, currency), devices UI.
- **Catalog:** categories, variants, modifiers, tax categories, branch prices/availability, station routing, and
  cloud→gateway delivery.
- **Restaurant operations:** zones/tables/visits, item-level preparation states, KDS, cancellations.
- **Money:** sales, payments, refunds, cash sessions, registers, receipts, fiscal numbering (legal input needed).
- **Inventory:** stock movements and balances.
- **Reporting:** projections by business date, the consolidated dashboard.
- **SaaS:** plans, subscriptions, entitlements, signed offline entitlements, grace policy, billing provider.
- **Platform:** OpenAPI, CI, observability (metrics, health, alerts), hosted environments, secret management,
  signed installer/updates, localization (FR, AR/RTL), accessibility audit.

---

## 2. Tenant hierarchy and isolation (confirmed)

**Organization → Business → Branch** is kept (ADR-001). Mapping to the vocabulary in docs 01–03:

| Level | Meaning | Owns | Doc 03 term |
|---|---|---|---|
| **Organization** | The SaaS customer account: the **tenant** and the billing party | Subscription and entitlements, users and memberships, roles, audit, all businesses | `tenant` (`tenant_id` ≡ `organization_id`) |
| **Business** | A brand or legal entity inside the account (e.g. a café brand and a pizzeria brand) | Legal/fiscal identity (tax id, invoice header), **catalog**, tax configuration, default currency | (implicit in doc 03) |
| **Branch** | A physical site with exactly one active gateway | Timezone, business date, zones/tables, stations/printers, registers, terminals, branch prices/availability, stock | `branch` |

**Isolation strategy (unchanged principles, made explicit):**
1. Every tenant-owned row carries `organization_id`. Business-scoped rows add `business_id`, branch-scoped rows add both plus
   `branch_id`. Composite foreign keys reference the parent's full key, so a row can never point into another tenant. The
   pattern is already in place for orders, lines and branch products.
2. **The scope comes from the authenticated principal**, never from the request body: gateway credential, user
   membership plus branch assignment, device token, QR session.
3. **Deny by default:** an out-of-scope resource returns 404 (no probing), and a missing permission returns 403.
4. **Cross-tenant negative tests** are mandatory for every new endpoint. They extend `MultiTenantTests`.
5. **PostgreSQL row-level security** is added as defence in depth at milestone 2.1. The app sets
   `app.organization_id` per transaction, and policies apply to the `pos_app` role (ADR-003). Platform admin uses a separate role and audited
   access.
6. The gateway holds **one branch only**. Isolation there is physical: a separate database per branch.

---

## 3. Production module boundaries (cloud modular monolith)

ADR-002: **one deployable cloud host (`Pos.Cloud`) with internal modules**, not microservices. A module is a
folder and namespace with its own endpoints, EF entity configuration and services. It exposes a small public
interface; no module queries another module's tables directly. A module moves into its own class library only
when it needs separate dependencies or its size justifies it. One `DbContext` is acceptable while the modules share a
schema owner, with entity configurations kept per module; split if contention appears.

The gateway keeps its single-process structure and gains matching **branch-side** modules (orders, tables,
cash) built on the same command engine.

| Module | Owner of truth | Responsibilities | Main entities | Reuses | Key rules |
|---|---|---|---|---|---|
| **Identity & Access** | Cloud | Users, sign-in (ASP.NET Core Identity), MFA for owners/admins, sessions, password reset; **permissions catalog**, roles, branch-scoped assignments; policy evaluation; audit log | `users`, `organization_memberships`, `roles`, `role_permissions`, `staff_assignments`, `audit_events` | Report-key scoping pattern, 404-on-out-of-scope | Permission codes are data (`orders.submit`, `cash.close`, …). Platform admin is a separate scheme. Every privileged change is audited. |
| **Organization management** | Cloud | Organization, business and branch lifecycle and settings (legal/fiscal identity, timezone, currency, business-day cutoff); devices: gateways, terminals, provisioning, revocation | `organizations`, `businesses`, `branches`, `gateway_credentials`, `provisioning_codes`, `device_registrations` | Existing tables, gateway provisioning and fencing | Moves the CLI logic behind an API. One active gateway per branch stays a database invariant. |
| **Staff & permissions (branch)** | Cloud defines; gateway enforces offline | Staff profiles per branch, **POS PIN credentials** (hashed), role → permission snapshot delivered to the gateway | `staff_members`, `staff_pins` (hash only) | Config snapshot channel (ADR-004) | The gateway authorizes staff actions offline from the last snapshot. Revocation applies at the next snapshot. Operations record the staff actor. |
| **Catalog & branch pricing** | Cloud (business level) | Categories, products, variants, modifier groups/options, tax categories, branch overrides (price, availability, station routing); **versioned branch configuration snapshot** | doc 03 §5 tables | `branch_products` read model becomes the published snapshot | Prices resolved on the gateway from the snapshot (never by the client). Order lines keep label/price/tax snapshots. |
| **POS & shared orders** | Gateway (branch authority); cloud keeps history | Orders, lines, submissions, preparation routing, tables/zones/visits, KDS states, print intents; later sales, payments, cash sessions as gateway commands | gateway `shared_orders`… → doc 03 §6–8 | `OrderService`, operations journal, outbox, print pipeline | Every command has an operation id, an expected version and the staff actor. Financial records are append-only. Cloud-side edits to active branch state are not allowed (single writer). |
| **Inventory** | Gateway records movements; cloud keeps ledger + balances | Stock movements from sales/adjustments, balances projection, adjustments from the dashboard (as commands to the gateway, or cloud-side for non-operational stock decisions — ADR at 2.9) | `stock_movements`, `stock_balances` | Outbox event pattern | Movements only, never silent quantity edits. Replay-safe through the source operation id. |
| **Reporting** | Cloud (derived) | Projections from synced events by branch business date; consolidated owner views; exports | `report_*` projections | Existing read models | Rebuildable from events. Reports show **sync freshness** (last acked sequence per branch). |
| **Subscriptions & entitlements** | Cloud | Plans, prices, subscriptions, entitlement resolution, signed offline entitlement tokens for gateway/terminals, grace policy, billing provider adapter | doc 03 §10 tables | Gateway ECDSA trust (the cloud signs, the gateway verifies) | Entitlements are data, not code switches. Expiry never deletes data. The gateway enforces the last signed snapshot with bounded grace. |
| **Sync (existing)** | Shared | Gateway → cloud event ingest (exists); **cloud → gateway configuration snapshots** (new, same credential) | existing | All of it | Both directions are versioned, idempotent and resumable. |

**Dependency direction:** Identity & Access ← every module. Organization ← Catalog, Staff, Devices. Catalog → Sync
(snapshot) → Gateway orders. Orders/Inventory → Sync (events) → Reporting. Entitlements ← Organization; read by
Devices and Gateway.

---

## 4. Frontend application boundaries

ADR-008: separate Vite apps per surface, sharing a small **packages** layer through **npm workspaces**. The
design system stays **open for Claude Design**: semantic tokens as CSS variables with placeholder values. No
third-party component library is adopted before the Claude Design foundation is approved.

| App (folder) | Users | Talks to | Auth | Offline | Hosting |
|---|---|---|---|---|---|
| **Owner/manager dashboard** (`apps/dashboard-web`) | Owner, branch manager | Cloud management API | Cookie session (Identity), MFA, CSRF protection | Online only | Static files from the cloud host (same origin) |
| **Windows POS** (`apps/pos-web`, existing) | Cashier, waiter at the counter | Bridge → Pos.Core → gateway | Device pairing (exists) + staff PIN (2.3) | LAN-offline through the gateway | WebView2 virtual host (exists) |
| **Waiter mobile** (`apps/waiter-web`) | Waiter | Gateway LAN API + SignalR | Device pairing (like terminals) + staff PIN | LAN-offline | Served by the gateway over LAN HTTPS. **Needs the LAN TLS decision** (ADR-009). |
| **Kitchen display** (`apps/kds-web`) | Kitchen/bar staff | Gateway LAN API + SignalR | Device pairing (station-scoped device) | LAN-offline | Served by the gateway, browser kiosk on a PC/tablet |
| **Customer QR** (`apps/qr-web`) | Guests | Cloud public QR API (forwarded to the gateway) | Signed table token + guest session; rate limits | **Online only** (doc 01) | Cloud (CDN/static), separate origin from the dashboard |
| Super-admin (`apps/admin-web`, later) | Platform staff | Cloud platform API | Separate scheme, MFA, audited | Online | Separate origin |

Shared packages (created when a second app needs them, not before):
- `packages/ui`: semantic tokens (doc 04 §3 names) as CSS variables, plus primitives built from approved Claude Design boards.
  The values in `pos-web/src/index.css` are placeholders that get mapped to the token names.
- `packages/contracts`: TypeScript types for the gateway LAN and cloud APIs. They are generated from OpenAPI once the cloud
  exposes it (ADR-007); the gateway LAN contracts stay hand-written and tested until generation is worth it.
- Shared money/format helpers (`pos-web/src/lib/money.ts` moves here).

Each surface keeps its own state management, kept minimal (React state plus fetch/SignalR hooks as today). Adding a
router or a data library needs a written reason when that app is created.

---

## 5. API conventions

These apply to every new endpoint, cloud and gateway. Existing endpoints already follow most of them.

| Topic | Convention |
|---|---|
| Versioning | URL prefix `/v1`. Breaking changes → `/v2` side by side, with N-1 kept for gateways and terminals in the field until they upgrade. |
| Resources | Lowercase, plural, kebab-case paths. Hierarchy reflects scope: `/v1/businesses/{id}/products`, `/v1/branches/{id}/tables`. |
| JSON | `System.Text.Json` web defaults (camelCase), strict: unknown members rejected, max depth limited, 2 MB body cap (exists). |
| Identifiers | UUID strings generated client-side for commands and offline-created entities (UUIDv7 once library support is verified). |
| Money / time | Integer minor units plus ISO 4217 currency (TND has 3 decimals); UTC ISO-8601 timestamps; the branch IANA timezone and business date are explicit fields. |
| Commands | Body carries `operationId`, and `expectedVersion` where it modifies a versioned entity. Replays return the original result; unknown outcomes are settled with `/operations/{id}/resolve` (existing pattern). Cloud management writes accept an `Idempotency-Key` header. |
| Concurrency | Optimistic: `version` on entities; a conflict returns 409 `version_conflict` with the current state (exists). |
| Errors | One envelope `{ "code": "snake_case", "message": "<localized text>", "current"?: … }` (exists as `GatewayError`/`CloudError`). 400 validation, 401 unauthenticated, 403 forbidden, 404 not found or out of scope, 409 conflict, 413, 423 recovery, 429, 503 transient. No internals in responses. |
| Lists | Cursor pagination `?after=<cursor>&limit=<1..100>`, with a `next` cursor in the response; filters as query parameters; no offset paging on large tables. |
| Auth | Cloud dashboard: cookie + antiforgery. Programmatic clients: bearer (report keys → scoped API keys). Devices: existing signed or bearer schemes. Scope always comes from the principal. |
| Real-time | SignalR carries notifications only, never the source of truth: clients reload snapshot + cursor after reconnecting (exists). |
| Documentation | Cloud: OpenAPI from `Microsoft.AspNetCore.OpenApi` (first-party), published per version. Gateway: contract tests remain the reference until it exposes OpenAPI too. |
| Localization | API `message` in the user's language (FR first, AR, EN); clients switch on `code`, never on `message`. |

---

## 6. Migration strategy

- **Cloud PostgreSQL (EF Core):**
  - One migration per change, reviewed with its generated SQL.
  - **Expand → migrate → contract** for anything that existing clients still read.
  - Migrations run as an explicit deploy step (`Pos.Cloud migrate`) with the owner role; the app runs with the
    DML-only role (exists). There is no automatic migration at startup.
  - Data backfills are idempotent and batched. Every migration is tested against a copy of production-shaped data.
- **Gateway / terminal SQLite:**
  - Numbered, append-only migrations applied at start (exists). Forward-only.
  - The gateway takes a **verified backup before applying any migration** (reuses `GatewayBackup.Create`) and refuses to
    start on a schema newer than itself (exists).
  - Terminal migrations must never touch shared branch state.
- **Contracts:**
  - Sync events carry `schemaVersion`. The cloud accepts the current version and the previous one.
  - Gateways and terminals are upgraded in order: **cloud first, then gateway, then terminals**. A gateway refuses terminals newer
    than itself with an actionable message.
  - Configuration snapshots are versioned the same way.
- **Prototype data:** sample catalog ids are replaced by the first real configuration snapshot. Pilot branches start
  from clean databases; no migration path is promised for prototype installations.

---

## 7. Environment configuration

Principle (kept from Phase 1.4): **configuration only from code defaults plus environment variables**, with secrets never
in files that are committed or shipped. Production secrets come from the hosting secret store, which injects them as environment variables.

| Component | Setting | Today | Production |
|---|---|---|---|
| Cloud | `POS_CLOUD_DB` (DML role), `POS_CLOUD_MIGRATION_DB` (owner) | Env | Secret store |
| Cloud | TLS certificate | `--certificate` pfx + `POS_CLOUD_CERT_PASSWORD` | TLS terminated by the platform proxy with a public CA certificate, or a Kestrel certificate from the secret store |
| Cloud | Identity data-protection keys, cookie settings, entitlement signing key | Sessions/tokens need no key ring (ADR-015/016); entitlement key — | Persisted protected key ring; signing key in a KMS/HSM-backed store, rotated, never in the database |
| Cloud | Public base URLs (dashboard, QR), allowed origins | `POS_CLOUD_PUBLIC_ORIGIN` (dashboard), `POS_CLOUD_MAIL_DIR` or `POS_CLOUD_SMTP_*` + `POS_CLOUD_MAIL_FROM` (2.1C) | Env, per environment |
| Cloud | Data-protection keys for secrets at rest | `POS_CLOUD_DATA_KEYS` (2.1C, ADR-020) | Secret store; rotate with `data-key-new` + `mfa-rekey` |
| Gateway | `POS_GATEWAY_DATA_DIR`, `--listen`, sync options | Env/CLI | Set by the installer; the service runs under its own account (exists in the install script) |
| Gateway | Cloud URL, credential | `cloud-link` (DPAPI) | Same; certificate pin optional once a public CA is used |
| POS | Data directory, WebView2 runtime | Defaults + guard | Installer-managed |
| All | Fault injection (`POS_*_FAULT*`) | Debug builds only | Compiled out (exists) |

Environments: **dev** (local, private PostgreSQL, dev certificates), **test** (automated, throwaway clusters, as
today), **staging** (hosted, production-like, synthetic tenants), **production**. Each environment has
its own signing keys and certificates; no data is copied from production to lower environments without anonymization.

---

## 8. Deployment assumptions

- **Cloud:**
  - `Pos.Cloud` (net10.0, cross-platform) runs as a container or app service behind a TLS-terminating proxy,
    with ≥ 2 instances once stateless. It is stateless already except for data-protection keys (to be persisted).
  - Managed PostgreSQL with automated backups and point-in-time recovery, in a region chosen for **data residency
    and latency to Tunisia**. This is an open decision (legal input), recorded in ADR-012.
  - Background work runs as hosted services in the same host at first, with a database-backed outbox for side effects. There is no Redis
    or message broker until measured need.
- **Branch:**
  - One Windows PC per branch runs `Pos.Gateway` as a Windows Service (the install script exists), on a UPS.
  - Backups go to a share or device off the gateway disk (doc 08).
  - Terminals run the WPF app. Waiter and KDS devices use browsers on the branch LAN.
- **Distribution:** a signed installer and update channel for the POS and gateway, with updates deferred during an open business
  day and rollback supported. Not built yet.
- **Capacity assumption** (doc 09): one gateway comfortably serves a café-restaurant (≈ 340 ops/s measured on
  loopback against typical needs of a few ops/s). Cloud ingest measured at ≈ 136 events/s per branch on one PC. Hosted
  capacity must be measured in staging with several branches syncing at once.

---

## 9. Critical security and reliability requirements before production

**Security (blocking for the pilot):**
1. Real identity: Identity with strong hashing, MFA for owners and platform admins, lockout/rate limits,
   session revocation, secure password reset.
2. Server-side RBAC on every endpoint and gateway command, with branch scope and staff attribution; negative
   cross-tenant and cross-branch tests per endpoint; PostgreSQL RLS as defence in depth.
3. Audit log (append-only) for role changes, device registration/revocation, discounts, cancellations,
   refunds, cash adjustments, entitlement overrides and support access.
4. Public CA TLS for the cloud. A **decided and tested LAN trust model** for browsers (waiter/KDS) before those apps ship
   (ADR-009).
5. Encrypted, authenticated backups (doc 09 risk 4), and gateway data at rest protected beyond ACLs (evaluate SQLCipher-style
   encryption on target hardware).
6. Signed offline entitlements with bounded grace and clock-rollback resistance.
7. QR endpoints: signed table tokens, per-session and per-table rate limits, staff approval by default, guest privacy.
8. Supply chain: CI with dependency and secret scanning, pinned versions (exists), signed installers.
9. Payments: no card data anywhere; provider certification before any card flow is enabled (doc 01 §16).

**Reliability (blocking for the pilot):**
1. Monitoring and alerting: gateway heartbeat, sync lag, backup failures, print queue age, cloud error rate.
   Today these exist only as logs and `status`.
2. Physical hardware checklist of doc 09 §7 executed on the target PCs, printers and network.
3. Measured RPO/RTO targets agreed, and restore drills of the cloud database and of a gateway.
4. Pre-migration gateway backup; upgrade ordering enforced; rollback tested.
5. Faster detection of silent network loss for tills (doc 09 risk 1), at least a clear "connection unstable" state.
6. Fiscal compliance (Tunisian VAT, receipt/invoice numbering, retention) confirmed by qualified experts **before**
   the sales/payments schema is migrated.

---

## 10. Prioritized implementation roadmap

Each milestone is a vertical slice: schema + API + gateway (if any) + UI only where needed + tests + docs.
It ends with all existing suites green (Debug, Release, E2E). Milestone numbers continue the phase
numbering; they map onto doc 05 M2–M9.

| # | Milestone | Scope | Depends on | Exit criteria |
|---|---|---|---|---|
| **2.1** | **Identity, access and audit foundation** | Users and organization memberships (Identity), roles, permissions catalog, branch-scoped assignments, policy evaluation, audit log; management API for organization/business/branch (replacing CLI-only creation); `/v1` conventions, error envelope, cursor paging, OpenAPI; RLS on `pos_app`; **CI pipeline** running the existing suites | — | Owner signs in (MFA), creates a business and two branches, invites a manager scoped to one branch. Cross-tenant/branch negative tests and RLS tests pass. Audit records every change. CI green. |
| 2.2 | Catalog & branch pricing + config sync | Categories, products, variants, modifiers, tax categories, branch overrides, stations; **versioned configuration snapshot pulled by the gateway** (signed channel); the gateway and POS use it in place of `SampleData` | 2.1 | Two branches with different prices/availability. Gateway applies snapshots idempotently, keeps the last good one offline, rejects a snapshot for another branch. Order lines keep price snapshots. |
| 2.3 | Staff sign-in on POS and gateway authorization | Staff, PIN hashes and permission snapshot to the gateway; POS sign-in/out; every gateway command records the actor and checks permission | 2.1, 2.2 | A waiter cannot perform cashier-only actions, offline included. A revoked staff member is refused after the next snapshot. Operations carry staff ids to the cloud. |
| 2.4 | Tables, visits and order lifecycle | Zones/tables/visits on the gateway; item states; cancellations with audit; decide the fate of "Vente directe" | 2.2, 2.3 | Concurrency tests (one active visit per table), replay tests and E2E on two tills. |
| 2.5 | Checkout, cash sessions and receipts | Sales, payments (cash and manual card record only), refunds, registers, cash sessions, receipts; fiscal numbering per the expert review | 2.4 + **fiscal review** | No duplicate sale or payment under retries. Totals reconcile across session, branch and cloud. Append-only financial history. |
| 2.6 | Reporting & owner dashboard v1 | Projections by business date, sync freshness, dashboard app (catalog, staff, branches, reports) on approved Claude Design boards | 2.1–2.5, **design foundation approved** | Reports reconcile to synced sales. Dashboard role/branch filtering is tested. |
| 2.7 | Kitchen display | `kds-web` served by the gateway, station devices paired, preparation states | 2.4, ADR-009 | Station routing, reconnect, printer + KDS together, no lost tickets. |
| 2.8 | Waiter mobile | `waiter-web` on the LAN with device pairing + PIN | 2.3, 2.4, ADR-009 | Phone on Wi-Fi with no internet completes table orders; silent Wi-Fi loss shows clearly. |
| 2.9 | Inventory (basic) | Stock movements from sales/adjustments, balances, dashboard adjustments | 2.5 | Replay-safe movements; the projection rebuild matches the ledger. |
| 2.10 | Subscriptions & entitlements | Plans/entitlements as data, signed offline entitlements, grace, manual verified activation first, provider adapter later | 2.1 | Limits enforced (branches/terminals). Expiry and grace behave as specified offline. No data deletion. |
| 2.11 | Customer QR ordering | Signed table tokens, guest sessions, staff approval, cloud → gateway forwarding | 2.4, 2.6, 2.10 | Abuse/rate-limit tests, guest privacy test, outage message. |
| 2.12 | Production operations | Monitoring/alerting, staging, signed installer/updates, encrypted backups, restore drills, doc 09 §7 hardware checklist, security review | Runs alongside 2.6+ | Pilot readiness review (doc 05 M9). |

Parallel tracks that need no code: **Claude Design foundation** (tokens, shells; doc 04 §15 steps 1–2) can start
now and must be approved before 2.6. **Fiscal/legal review** must finish before 2.5. **Payment provider
investigation** must finish before any card flow. The **LAN TLS trust decision** (ADR-009) must be made before 2.7/2.8.

### Recommended next milestone: **2.1 Identity, access and audit foundation**

Every other module depends on knowing *who* acts *in which scope*, and the current only reading credential (report
keys) cannot carry users, roles or branch assignments. It also adds the cross-cutting pieces each later milestone needs:
API conventions in code, OpenAPI, RLS and CI. Suggested first slice inside 2.1:
1. CI running the existing suites.
2. Identity users + memberships + roles/permissions schema with RLS.
3. Management API for organization/business/branch with audit.
4. Cross-tenant tests extended to the new endpoints.

Phase 2.1A (CI, error envelope, `/v1` group, OpenAPI, configuration validation) is done: see
[11-CI-AND-API-FOUNDATIONS.md](11-CI-AND-API-FOUNDATIONS.md). Phase 2.1B (cloud user authentication: Identity,
sessions, MFA, password reset, invitation-only owner onboarding, audit) is done: see
[12-CLOUD-AUTHENTICATION.md](12-CLOUD-AUTHENTICATION.md). RBAC, branch scopes and the membership API: Phase 2.1D, [14-CLOUD-ACCESS-CONTROL.md](14-CLOUD-ACCESS-CONTROL.md). RLS: Phase 2.1E, [15-CLOUD-ROW-LEVEL-SECURITY.md](15-CLOUD-ROW-LEVEL-SECURITY.md). The organization/business/branch management API remains.

---

## 11. Open decisions (owners needed)

| Decision | Needed before | Notes |
|---|---|---|
| LAN TLS trust for browsers (ADR-009) | 2.7 / 2.8 | Options: local branch CA installed on devices; a per-branch public hostname with a CA certificate resolving to the LAN IP; a thin native wrapper. Prototype on real phones. |
| Fiscal numbering, VAT, receipt content, retention | 2.5 | Tunisian experts. |
| Card payment provider | Any card flow | Certified provider and flow (doc 01 §16). |
| Cloud region / data residency, RPO/RTO | 2.12 (staging earlier) | ADR-012. |
| Offline entitlement grace length, clock handling | 2.10 | Product + legal. |
| Inventory adjustments: cloud-originated vs gateway commands | 2.9 | Single-writer rule for operational stock. |
| Gateway at-rest encryption | 2.12 | Measure on target hardware. |
