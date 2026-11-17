# SaaS POS — System Architecture

**Version:** 1.0 — proposed technical baseline  
**Date:** 2026-11-17  
**Status:** Architecture proposal; requires Phase 1 prototype and security review before production  
**Source of truth:** `01-PRD.md` for product behavior and confirmed business decisions.

## 1. Architecture goals and constraints

Build a Tunisia-first, internationalization-ready, multi-tenant SaaS for café-restaurants. The product must feel premium and fast while preserving financial correctness, security, operational simplicity, and business continuity during internet outages. The MVP includes multiple branches, multiple Windows cash registers per branch, mobile waiter ordering, customer QR ordering (internet required), kitchen/bar/counter printing and displays, and configurable subscription entitlements.

**Guiding decisions**
- ASP.NET Core **modular monolith** in the cloud; avoid microservices for the MVP.
- Windows POS: **.NET WPF host + WebView2 + React/TypeScript**. WPF is a thin host, not a second full UI.
- React/TypeScript shared UI primitives across web, POS, waiter and kitchen interfaces, with different task-optimized layouts.
- Cloud **PostgreSQL** is the canonical long-term business record; branch-local **SQLite** enables outage operation.
- A designated **.NET Windows Service gateway** coordinates all branch-local shared writes during WAN outages and ordinarily serves as the branch operational coordinator.
- Payment authorization and sensitive subscription state are verified by trusted server/provider processes, not by React.
- Implement financial actions as immutable events or controlled reversals; do not overwrite completed transactions.

## 2. Logical topology

```text
                     INTERNET / CLOUD
   +------------------------------------------------+
   | React owner dashboard   React SaaS super-admin  |
   | React customer QR       Public API entry        |
   |                     HTTPS/TLS                   |
   | ASP.NET Core modular monolith                   |
   | Identity | Tenants | Catalog | Orders | Sales    |
   | Payments | Inventory | Billing | Devices | Sync  |
   | PostgreSQL | object storage | job workers       |
   +------------------------+-----------------------+
                            |
                    authenticated sync
                            |
                 BRANCH LOCAL NETWORK
   +------------------------+-----------------------+
   | .NET Gateway Windows Service (designated PC)    |
   | Local SQLite + durable command/event log        |
   | Local HTTP(S)/SignalR + sync/print queues        |
   +----------+---------------+---------------------+
              |               |             |
     Windows POS 1..N   Waiter mobile   Kitchen/KDS
     WPF/WebView2       local Wi-Fi     printers
     React + .NET       browser         and displays
     terminal cache
```

**Important boundaries:** Customer QR ordering reaches the cloud and is forwarded to the branch gateway while connectivity exists. It is **not** available when the branch cannot reach the cloud in the MVP. A branch gateway is one per branch; branches are isolated from each other when offline.

## 3. Deployable components

| Component | Runtime | Responsibility |
|---|---|---|
| Cloud API | ASP.NET Core | Tenant/branch authorization, catalog, orders, financial records, reporting, device and entitlement management |
| Cloud workers | .NET hosted services or dedicated worker | Outbox processing, notifications, reconciliation, report projections, payment callbacks |
| PostgreSQL | Managed or hardened self-hosted | Canonical tenant/branch data, financial records, cloud sync checkpoints |
| Owner dashboard | React/TypeScript | Branch configuration, staff, reports, catalog, subscriptions |
| Super-admin | React/TypeScript | Platform operations with separate privileged authorization |
| Customer QR | React mobile web | Table-scoped customer session and online ordering |
| Windows POS | WPF + WebView2 + React | Touch-first checkout, cash sessions, printing control and device status |
| Local gateway | .NET Windows Service | Branch coordination, durable local writes, LAN APIs, print routing, sync |
| Waiter mobile | React responsive web/PWA-style shell | Authenticated table ordering; LAN fallback requires deliberate endpoint discovery and TLS trust |
| Kitchen display | React browser/tablet/PC | Station tickets and preparation updates via local gateway/cloud when appropriate |

**MVP infrastructure:** containerized cloud API/workers, PostgreSQL with automated backups and tested restore, HTTPS reverse proxy, structured logs, metrics, alerts, secure object storage for approved assets. Redis is optional, not a prerequisite; use database-backed outbox first.

## 4. Codebase layout

```text
saas-pos/
  apps/
    api/                 # ASP.NET Core API + domain modules
    web-dashboard/       # React owner/manager
    super-admin/         # React platform administration
    customer-qr/         # React public mobile ordering
    waiter-web/          # React waiter UI
    kitchen-display/     # React KDS
    windows-pos/         # WPF host, WebView2, native integrations
    branch-gateway/      # .NET Windows Service, local API, sync, print
  packages/
    ui/                  # shared design tokens and React primitives
    api-client/          # generated typed API clients
    contracts/           # shared TS schemas/contracts (generated where feasible)
  tests/
    integration/
    offline-recovery/
    hardware/
    security/
  infrastructure/
    docker/
    deployment/
    observability/
  docs/
    01-PRD.md
    02-SYSTEM-ARCHITECTURE.md
    03-DATABASE-DESIGN.md
    04-UI-UX-SPECIFICATION.md
    05-DEVELOPMENT-ROADMAP.md
    DECISIONS.md
```

Use explicit .NET projects/modules for identity, tenancy, branches, catalog, orders, preparation, sales, payments, cash sessions, inventory, billing, devices, sync, audit and reporting. Module boundaries communicate through interfaces/domain events; no cross-module database access without a defined contract.

## 5. Data ownership and consistency

### 5.1 Online mode
1. POS and waiter write branch operational commands to the **branch gateway**, which durably records them in its SQLite transaction log before acknowledging.
2. Gateway updates branch state and publishes station events to other terminals over LAN.
3. Gateway asynchronously syncs committed operations to cloud API using authenticated, idempotent batches.
4. Cloud validates tenant, branch, device, permissions, sequence, business rules and schema version; persists accepted operations transactionally in PostgreSQL.
5. Cloud responds with per-operation outcomes and durable sync checkpoints; gateway retries only unacknowledged operations.

**Design clarification:** Cloud PostgreSQL is canonical for global reporting and historical records; during an outage the branch gateway is the *temporary authoritative writer* for branch operations. Reconciliation is deterministic and explicit. Cloud-initiated edits to active branch state must be serialized or version-checked to avoid dual-writer conflicts.

### 5.2 Local storage
- Gateway SQLite: operational state, command/event journal, outbox, print jobs, branch catalog snapshot, entitlements, sync checkpoints.
- Terminal SQLite: minimal encrypted/protected cache, terminal settings and optionally a **restricted emergency queue**. It must **not** independently modify shared tables/orders while gateway is healthy.
- Never put secrets, card data or privileged signing keys in React/localStorage.
- Use SQLite WAL where appropriate, transactions, schema migrations, integrity checks and controlled backup/recovery.

### 5.3 Idempotency and ordering
- Client-generated UUID/ULID for each command and stable domain entity identifiers.
- Unique `(tenant_id, branch_id, operation_id)` constraint in cloud and gateway.
- Monotonic per-branch log position and per-entity expected version for concurrency control.
- Acknowledgment only after durable commit; exponential retry with bounded jitter and dead-letter review.
- Outbox/inbox patterns for reliable side effects; exactly-once *business effect* through deduplication, not exactly-once network delivery.
- Stock is recorded as movements; finalized payments/sales are reversed or refunded through new entries, not mutated away.

## 6. Offline, gateway availability and failover

**WAN down, gateway/LAN up:** Windows POS, waiter mobile, tables, local cash operations, basic product stock, KDS and printers continue through gateway. QR ordering and cloud-only management actions are unavailable. Card payment depends on certified provider/terminal connectivity and is never assumed offline.

**Gateway down, WAN up:** Clients should fail closed for conflicting shared writes unless a tested cloud-direct fallback with authoritative branch ownership transfer is available. MVP can show a clear degraded mode and require gateway recovery; no silent dual-write mode.

**Gateway down, WAN down:** No guarantee of multi-terminal consistency. MVP should block shared-table operations and offer only a tightly scoped emergency single-terminal cash-sale mode **if implemented and tested**; otherwise stop transactional writes and provide recovery instructions. This is a design gate, not an unqualified MVP promise.

**Gateway recovery:** Service auto-start, watchdog/health checks, persistent journal, safe restart, replay and reconciliation. Define and test backup/restore of the designated gateway PC. Do not implement automatic active-active gateway election in MVP. Provide an assisted, controlled gateway reassignment/recovery procedure.

**Waiter LAN access:** Local Wi-Fi network, authenticated sessions, device discovery/configuration, TLS certificates/trust, secure CORS/origin policy and explicit LAN fallback UX must be prototyped on actual phones. A normal HTTPS cloud-origin web page cannot be assumed to call an arbitrary insecure LAN HTTP endpoint.

## 7. Orders and preparation architecture

**Separate state machines:** table visit, order submission, line-item preparation, preparation-ticket delivery and payment/settlement.

- A table visit groups multiple submissions and payments. Only new/changed lines create preparation events.
- Staff orders dispatch directly; customer QR follows branch-configured direct/approval mode (pilot default approval).
- Server/gateway validates product availability, modifiers, tax, prices, branch scope and idempotency.
- Each product maps to a station (kitchen, bar, counter, etc.); one submission may fan out into several station tickets.
- Print and KDS deliveries are durable jobs with separate status. Do not claim printer success based only on socket send.
- Ambiguous print outcomes require operator review and controlled reprint, not infinite automatic reprinting.
- Post-dispatch cancellations generate explicit adjustment notices to affected stations and an audit record.

## 8. Payments, cash and financial integrity

- Cash: cashier confirms receipt in a specific register session; waiter can request cash checkout but cannot confirm collection.
- Card: waiter may initiate through a **certified supported provider/terminal flow**. Mark paid only after trustworthy provider confirmation/reconciliation; do not capture raw card credentials.
- Use provider idempotency keys and webhook signature verification, replay protection and server-side status reconciliation.
- Maintain immutable payment records, refunds, cash movements, shift opening/closing and audit trails.
- Support partial and split payments; table closes only after verified settlement and no unresolved chargeable items.
- Store amounts in **integer minor units** with currency-specific precision (TND has 3 decimal places); explicit tax/rounding policy and immutable price/tax snapshots.
- Regulatory, receipt and tax compliance in Tunisia must be validated with qualified local advisors before launch.

## 9. Security architecture

**Trust boundaries:** Public QR clients are untrusted; authenticated staff clients are still untrusted; the gateway is a registered branch device, not an unrestricted authority; payment providers are external trust domains.

- Identity: ASP.NET Core Identity or compatible vetted identity solution, Argon2id/PBKDF2 appropriately configured, MFA for privileged staff, secure cookie/token lifecycle.
- Authorization: server-side permission checks with tenant + branch + device + operation scopes; deny by default. PostgreSQL RLS may add defense in depth where feasible.
- Gateway: per-device registration, rotating credentials, scoped certificates/tokens, local staff authorization, revocation behavior and audit logs.
- WebView2 bridge: narrow allowlisted message commands, strict origin checks, validate every payload, no generic arbitrary filesystem/process execution.
- TLS: cloud HTTPS everywhere; secured LAN channels and practical device certificate provisioning must be validated.
- Secrets: server secret store, OS-protected local storage (e.g., DPAPI), no credentials embedded in distributable builds.
- Protect PII and business data: least privilege, retention policy, backups encrypted at rest, controlled support access.
- Auditing: immutable append-style records for overrides, refunds, canceled items, permissions, device and entitlement changes.
- CI: dependency scanning, secret scanning, code review, tenant-isolation tests, threat modeling and restore drills.

## 10. SaaS entitlements and installer

- Business subscription has monthly/annual billing, configurable module/branch/terminal entitlements and separately invoiced assisted setup/hardware packages.
- Cloud verifies payment-provider callbacks and activates subscriptions; React never activates a plan by itself.
- Each POS terminal registers to a tenant and branch. Gateway registration is distinct from POS terminal registration.
- Devices receive short-lived, signed entitlement snapshots with bounded offline grace; server can revoke on reconnect. Define grace policy and clock-tampering protections before launch.
- After expiration: preserve access to historical records/exports per retention policy; restrict new operations after grace with a clear user experience.
- Signed Windows installer/update packages, pinned update channel, safe updates outside active shifts, migration rollback/recovery, guided onboarding and hardware test wizard.

## 11. Observability and operations

- Correlation IDs across UI, gateway and cloud operations; structured logs without card/secret data.
- Metrics: API latency/error rate, sync lag, gateway heartbeat, print queue age, payment reconciliation exceptions, cash-session discrepancies, failed logins, disk space.
- Alert on sustained sync failure, offline gateway, repeated print failures, failed backups and payment callback anomalies.
- Separate development/staging/production environments; versioned database migrations; staged rollouts; regular restore exercises.
- Operational runbooks for WAN outage, gateway crash, printer failure, payment ambiguity, compromised device and subscription dispute.

## 12. API and contract approach

- Versioned HTTP/JSON API documented with OpenAPI; generated typed TS clients.
- SignalR for near-real-time status, **not** the source of truth. Clients recover state through snapshot + cursor after reconnect.
- Cloud API has authenticated staff endpoints, narrowly scoped public QR endpoints, provider callback endpoints and registered gateway sync endpoints.
- Local gateway API has device-authenticated POS, waiter and KDS endpoints, with explicit local authorization.
- Contracts versioned for backward compatibility during staggered terminal upgrades; server rejects incompatible schema versions with actionable guidance.

## 13. Performance and UX budgets (initial targets, validate on pilot hardware)

- Local product selection responds perceptibly immediately; target <100 ms UI feedback under normal load.
- Typical local order submission acknowledgment target <500 ms on healthy LAN, excluding physical print completion.
- Startup, navigation, large catalog search and printing tested on representative low-spec Windows touchscreen hardware.
- No decorative animation blocks cashier input; support reduced-motion preference.
- All critical operations show durable/queued/failed status clearly; never show success before the required persistence boundary.

These are engineering targets, not guaranteed SLAs. Set production SLOs after measured pilots.

## 14. Phase 1 validation gates — before full implementation

1. WPF/WebView2 hosts React with smooth touch and keyboard input; secure bridge proof of concept.
2. Windows installer works on representative target POS PC, including WebView2 runtime provisioning.
3. Gateway Windows Service + SQLite survives restart without losing acknowledged operations.
4. Two POS terminals concurrently modify different orders and safely reject conflicting edits to the same order.
5. Waiter phone on local Wi-Fi connects securely to gateway with cloud unavailable.
6. Kitchen/bar test printers receive correctly routed tickets; simulate printer disconnect and uncertain acknowledgment.
7. Cloud sync retries after WAN outage without duplicate sales/tickets and reconciles checkpoints.
8. Simulate gateway failure, recovery and controlled reassignment; document actual degraded behavior.
9. Validate one supported Tunisian waiter-initiated card payment path; if none exists, mark as external dependency and do not fake card success.
10. Test tenant/branch isolation, invalid QR tokens, authorization bypass attempts and signed update verification.

**Go/no-go:** Proceed to complete UI production design and main implementation only after high-risk desktop, LAN, printing and sync paths are demonstrated. Card-provider and fiscal compliance gaps must have explicit owners and release gates.

## 15. Open decisions and dependencies

| Item | Proposed default / required validation |
|---|---|
| .NET version | Choose current supported LTS at implementation; pin SDK/runtime |
| UI package versions | Pin React, WebView2 SDK, UI components and animation library |
| Gateway hosting | Designated on-site Windows PC with UPS recommended; recovery runbook required |
| LAN certificate onboarding | Prototype secure phone-to-gateway connection and trust setup |
| Payment provider | Verify Tunisian certified mobile/terminal flow, webhook semantics and fees |
| Fiscal compliance | Local tax, invoice/receipt, archiving and data rules reviewed before pilot |
| Offline grace | Choose exact duration and expiry UX before commercialization |
| Gateway failure policy | Prototype controlled emergency mode vs operational pause |
| Data retention and backup RPO/RTO | Define measurable requirements before production |
| Hardware matrix | Approve target printers, drawer interfaces, scanners, Windows versions |

## 16. Change control and next deliverables

- Any architecture change requires a short Architecture Decision Record (ADR) in `DECISIONS.md` stating context, alternatives, decision, consequences and migration impact.
- Next document: `03-DATABASE-DESIGN.md`, including tenant/branch constraints, branch event log, sync inbox/outbox, table visits, preparation jobs, payments and entitlements.
- Then `04-UI-UX-SPECIFICATION.md` and `05-DEVELOPMENT-ROADMAP.md`.
- Phase 1 prototype must validate the riskiest integration points before committing to the full build.
