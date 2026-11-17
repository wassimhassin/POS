# SaaS POS — Product Requirements Document (PRD)

**Version:** 1.0 (baseline draft for approval)  
**Date:** 2026-11-17  
**Initial market:** Tunisia; international expansion planned  
**First vertical:** Café-restaurants (also applicable to cafés and restaurants)  
**Status:** Business decisions confirmed; technical and regulatory details subject to validation

## 1. Product vision
Build a secure, reliable, visually polished, easy-to-use, multi-tenant SaaS point-of-sale and business-management platform for Windows touchscreen cash registers, managers on the web, waiters on mobile devices, and customers ordering from table QR codes. Begin with café-restaurants, then expand to mini-markets and general retail through modular capabilities rather than separate products.

### Success principles
1. Fast, predictable everyday POS interactions, including on modest Windows hardware.
2. Financial correctness and auditability ahead of decorative features.
3. Reliable branch-local operations during internet outages.
4. Simple self-service installation and guided onboarding.
5. Strong tenant/branch isolation and secure payment workflows.
6. Consistent premium React design, restrained animation, touch accessibility.

## 2. Confirmed decisions
| Area | Decision |
|---|---|
| Market | Tunisia first; internationalization-ready |
| Business model | Monthly/annual SaaS; optional one-time assisted installation and paid hardware/setup packages |
| Onboarding | Self-service Windows installer, optional assisted setup |
| First vertical | Café-restaurant |
| POS desktop | .NET WPF host with WebView2 embedding React + TypeScript |
| Backend | ASP.NET Core modular monolith |
| Databases | PostgreSQL cloud; SQLite for offline branch/terminal persistence |
| Local coordination | Designated on-site .NET Windows Service gateway for LAN operations |
| Interfaces | SaaS super-admin, owner/manager web dashboard, Windows POS, waiter mobile, customer QR web menu, kitchen display |
| Orders | Windows POS and waiter mobile submit directly to stations; QR direct or staff-approved (pilot default approval) |
| QR privacy | Customer sees own submitted items, not the entire table bill without further verification |
| QR additional orders | Allowed while table session is open |
| Preparation | Configurable kitchen/bar/counter routing to printer, display, or both |
| Cash registers | Multiple per branch from MVP |
| Inventory | Basic product-level stock; ingredient/recipe depletion later |
| Catalog | Variants, sizes, modifiers/extras and price adjustments in MVP |
| Branches | Full multi-branch management in MVP |
| Payments | Waiter initiates card payment via compliant provider; cash received and confirmed at cashier |
| Offline | Professional LAN offline for POS, waiter devices and preparation stations; customer QR remains internet-dependent in MVP |
| Licensing | Configurable entitlements, registered terminals, controlled offline grace, historical data access after expiry |

## 3. Personas and access surfaces
- **Platform administrator:** Operates tenant onboarding, plans, entitlements, billing status, devices, support and platform monitoring; access to business data must be narrowly scoped and audited.
- **Business owner:** Manages all authorized branches, subscription, catalog, staff, reports and settings.
- **Branch manager:** Manages assigned branches, staff and operational exceptions under granted permissions.
- **Cashier:** Opens/closes cash sessions, processes cash and permitted other payments, handles receipts and authorized adjustments.
- **Waiter:** Uses mobile or Windows POS to manage tables and submit orders; can initiate supported card-payment flow but cannot confirm cash collection.
- **Kitchen/bar/counter staff:** Receives preparation tickets and updates preparation state.
- **Customer:** Scans a table QR code, browses menu, orders and sees only items linked to their own ordering session.

A user may have multiple roles. All permissions are enforced server-side and scoped to tenant and branch. Default roles are configurable; owner-only billing actions remain protected.

## 4. Applications and UX requirements
### 4.1 Windows POS
- Fast touch-first product grid, category navigation, search, quantity controls, modifiers, order summary and checkout.
- Tables by zone with clear states (available, occupied, reserved, cleaning, disabled), active table sessions and transfer operations with audit trails.
- Cashier workflows, receipt printing, local network status, sync status, printer status and error recovery.
- Full keyboard/mouse support, accessible focus states and large touch targets.
- Avoid blocking network calls on critical UI interactions; local durable acknowledgment before claiming offline success.

### 4.2 Owner/manager web dashboard
- Business and branch configuration, staff, catalog, per-branch prices/availability, stock, reporting, cash reconciliation, devices and subscription.
- Branch selector and consolidated reporting with clear separation of gross sales, discounts, refunds, taxes and net sales.

### 4.3 Waiter mobile
- Authenticated, branch-scoped responsive interface; select table, submit/add items, view order/preparation status, request checkout and initiate supported card payments.
- During internet outages, communicate with branch gateway over local Wi-Fi; secure device/session authorization required.

### 4.4 Customer QR menu
- Mobile browser, no app or account required; table-specific menu and ordering session.
- Server-side product availability, modifier and price validation; prevent duplicate submissions.
- Show only that session's submitted items; full table bill requires a future verified access mechanism.
- QR mode configurable: direct dispatch or staff approval; pilot default is staff approval.
- QR ordering requires internet in MVP; communicate outage/unavailability clearly.

### 4.5 Kitchen display and print service
- Station-specific tickets and statuses; new, preparing, ready, served as appropriate.
- Printer and KDS can run together; item routing to kitchen, bar, counter or other configured stations.
- Pending print queue, delivery state and operator-visible ambiguous print outcomes; never silently discard tickets or blindly reprint uncertain tickets.

### 4.6 SaaS super-admin
- Manage tenants, subscription catalogs, entitlements, billing state, registered devices, support actions and operational alerts.
- Privileged actions logged; avoid unrestricted access to tenant sales data by default.

## 5. Restaurant operational requirements
### 5.1 Tables and visits
- Each branch defines zones and tables; a table can have at most one active visit/session unless an explicitly designed shared/merged-table operation applies.
- Table sessions group multiple order submissions and associated payments.
- Support table transfer, authorized order transfer, table closure and re-opening via explicit audited workflows.

### 5.2 Order lifecycle
- Orders can originate from Windows POS, waiter mobile or customer QR.
- Staff submissions dispatch to preparation stations without cashier approval.
- QR submissions follow the branch-configured approval policy.
- Additional submissions append to the open table session; only new items generate new preparation tickets.
- Order status, per-item preparation status, ticket delivery status and payment status are separate state machines.
- Item notes and modifiers are printed/displayed at the appropriate station.
- Before dispatch, authorized staff may edit items. After dispatch, changes create explicit correction/cancellation events and station notifications; never erase ticket history.
- Cancellation after preparation begins may require kitchen confirmation or manager approval based on policy.

### 5.3 Kitchen/bar/counter routing
- Route items based on configurable station assignment; support multiple printers and KDS screens per branch.
- Ticket metadata: unique ID, branch, table/session, submission, time, items, quantities, modifiers, notes and change/cancellation markers.
- Acknowledge tickets durably, retry safe failures and surface ambiguous physical-print outcomes for staff review.

## 6. Catalog and inventory
- Shared tenant catalog with branch-specific price and availability overrides.
- Product/category, SKU where relevant, images, description, tax classification, station routing, active/temporarily unavailable/archived status.
- Variants (size/format) with distinct prices, optional SKU and stock settings.
- Reusable modifier groups with required/optional selection, minimum/maximum choices and priced extras.
- Server validates every configured choice and computes authoritative price/tax totals; line items snapshot names, prices, taxes and modifiers at sale time.
- Basic stock by branch and stock location; record movements for receipts, sales, adjustments and returns rather than silently overwriting balances.
- Configurable oversell policy; non-stock-tracked prepared items supported.
- **Out of MVP:** Ingredient/recipe depletion, advanced procurement and inter-branch stock transfers.

## 7. Checkout, payments and cash
- Support cash, recorded card payments and mixed/split payments; item-based split bills where operationally appropriate.
- **Cash:** Only authorized cashier roles confirm receipt into an identified register cash session. Waiters may request cash checkout but not confirm collection.
- **Card at table:** Waiter initiates a certified provider/terminal/hosted payment flow; no raw card data handled by our app. Only trusted provider confirmation marks payment successful. Availability depends on validated Tunisian provider and hardware capabilities.
- Each register has independent opening/closing cash sessions, opening float, cash movements, counted close and discrepancy report.
- Payment and refund records have unique IDs and idempotency protection; no duplicate settlement on retry.
- Completed sales remain immutable; corrections via controlled refunds, reversals or adjustment records with reason, actor and timestamp.
- Receipts generated from finalized financial records; reprints identified/audited.
- A table closes only after all required items are resolved and outstanding balance is settled.
- Tips, if enabled, are recorded separately from product revenue.
- Exact Tunisian fiscal receipt/invoicing/VAT/retention obligations require local professional validation before production.

## 8. Multi-branch operations
- Owners create/manage multiple branches in MVP, each with its own zones, tables, terminals, printers, preparation stations, staff assignments, stock and cash sessions.
- Tenant-wide catalog with branch overrides; permissions and queries scoped by authorized branch.
- Branch-specific and consolidated sales/reporting with correct refunds/tax treatment.
- Multi-branch entitlements and pricing are centrally configurable.

## 9. Offline and local network requirements
- Each branch has a designated gateway Windows Service coordinating local cash registers, waiter clients, table/order state and print routing over LAN.
- Windows POS and gateway use durable SQLite-backed storage as defined by the architecture; the authoritative local write owner and sync protocol must be specified before implementation.
- Supported operations continue when WAN internet is down **if the branch LAN and gateway remain operational**.
- Customer QR web ordering remains cloud/internet dependent in MVP.
- Card transactions may require provider connectivity; never falsely show a declined/unverified card payment as settled.
- Cloud synchronization resumes automatically; use globally unique operation IDs, idempotency, ordered financial events, checkpoints, retry/backoff and reconciliation.
- Prevent double-close/double-pay across terminals using authoritative branch-local concurrency controls.
- Define and test gateway crash recovery, printer outage, local Wi-Fi loss, cloud outage, device restart and network partition behavior.
- **Fail-safe policy:** If both internet and gateway are unavailable, block unsafe shared-table/payment operations; define any restricted emergency mode explicitly during technical design, rather than implying full multi-terminal continuity.

## 10. SaaS subscriptions, activation and onboarding
- Flexible entitlements: modules, branches, terminals, staff limits and optional service tiers; plan labels/prices are data, not hard-coded feature switches.
- Monthly and annual cycles; optional one-time assisted installation and hardware/setup packages.
- Payment provider adapters and verified server-side activation; manual verified payments may be supported initially.
- Per-terminal enrollment to a tenant and branch, revocation and replacement flows, signed/time-limited offline entitlements and controlled grace periods.
- Renewal notices, expiry/grace policies and historical data access/export after expiry; no automatic data deletion solely due to subscription lapse.
- Guided first-run onboarding: create business/branch, assign staff, import/add products, configure zones/stations/printers, test receipt, register terminal, run a test sale.
- Signed Windows installer and secure update mechanism; defer updates during active sales and support rollback/recovery.

## 11. Security, privacy and reliability
- ASP.NET Core Identity or equivalent secure authentication, strong password hashing, MFA for privileged users, rate limiting and session/device revocation.
- Server-side RBAC and tenant/branch authorization on every API; cross-tenant tests mandatory. Consider PostgreSQL row-level security as defense in depth.
- TLS for internet traffic; authenticated/encrypted local communications and narrow WebView2-to-.NET bridge surface.
- No storage of raw payment-card data; payment integrations must use certified provider interfaces.
- Audit critical actions: discounts, cancellations, refunds, cash changes, role changes, device registration, subscription overrides and support access.
- Encrypted/protected secrets and local sensitive data; backup/restore testing, structured logs, monitoring, alerting and vulnerability/dependency checks.
- Security-conscious QR tokens, rate limits, duplicate protection and optional staff approval; QR presence alone is not proof of physical presence.
- Accessibility: readable contrast, keyboard navigation, touch sizing and localization-ready layouts (French first, Arabic RTL, later English).

## 12. Reporting requirements
- Daily and date-range sales by branch/register/cashier; product/category performance; payment method totals; discounts, refunds and taxes.
- Cash-session reconciliation and discrepancy review.
- Consolidated owner reports with branch filters; reports distinguish offline pending synchronization from confirmed cloud totals where applicable.
- Exports must obey role/branch permissions and fiscal retention rules.

## 13. Non-functional acceptance targets
These are **initial targets to validate on representative hardware**, not guarantees:
- Common POS interactions provide visible feedback immediately and avoid full page reloads.
- No acknowledged offline sale is lost across application restart or gateway restart in fault-injection tests.
- Duplicate sync attempts do not create duplicate orders, payments or stock movements.
- Cross-tenant and unauthorized branch access attempts are rejected in automated tests.
- Printer disconnects and ambiguous print acknowledgments are surfaced with recoverable operator actions.
- Every critical payment/cash change has a durable audit trail.
- Pilot must validate thermal printing, WebView2 rendering, touch interactions and LAN recovery on actual target Windows POS hardware.

## 14. MVP acceptance scenarios
1. **Staff order:** Waiter opens table, adds a variant with extras, sends; kitchen/bar receive only their assigned items.
2. **QR approval:** Customer scans table QR, orders, sees own items; waiter approves and tickets dispatch once.
3. **QR direct:** Restaurant switches to direct mode; valid QR order dispatches without staff approval.
4. **Add-on order:** Customer adds items later; only new items generate tickets, same table session remains open.
5. **Cancellation:** Authorized waiter cancels dispatched item; station receives change ticket, audit retains original.
6. **Split checkout:** Two payment methods settle a table without duplicate or overpayment; cashier cash session reconciles.
7. **Mobile card:** Waiter initiates card payment; only verified provider success settles it; failed/unknown status remains unresolved.
8. **Multi-register:** Two registers work against one branch gateway without double-closing a table.
9. **Offline WAN:** Disconnect internet; Windows POS, local waiter ordering and kitchen routing continue over LAN; queued events sync once online.
10. **Gateway failure:** Restart gateway; persisted work recovers; unsafe actions are blocked while coordination is unavailable.
11. **Multi-branch:** Owner sees separate and consolidated branch reports; manager cannot access unassigned branch.
12. **Inventory:** Sale generates tracked stock movement; modifier/variant price and stock rules are enforced.
13. **Subscription:** Unauthorized terminal cannot activate; authorized terminal operates within signed offline grace; expired entitlements follow configured restrictions.
14. **Onboarding:** New customer installs signed app, activates terminal, configures printer and completes test sale using guided setup.

## 15. Explicitly out of MVP
- Ingredient-level recipes and automatic ingredient depletion.
- Advanced procurement, supplier automation and inter-branch transfers.
- Loyalty programs, delivery marketplace integrations and advanced marketing.
- Fully offline customer QR ordering.
- Full multi-country tax/fiscal compliance modules beyond initial Tunisian launch.
- Unvalidated direct card-reader integrations or storing card credentials.
- Elaborate drag-and-drop floor-plan designer (start with zones and grid).

## 16. Dependencies, risks and unresolved implementation details
| Item | Risk / required validation |
|---|---|
| Tunisian card-at-table | Confirm certified provider, hardware, merchant eligibility and actual mobile initiation flow; do not promise unavailable integration. |
| Fiscal compliance | Validate Tunisian VAT, receipts, invoicing, retention and applicable fiscal-device requirements with local experts. |
| Gateway authority | Define single-writer/concurrency strategy, SQLite topology, leader/failover, and device discovery before coding. |
| Print delivery | Determine supported printer protocols, drivers, acknowledgment semantics and duplicate-safe recovery. |
| Offline entitlement | Define grace duration, clock rollback resistance, revocation and safe expiry behavior. |
| Pricing | Validate market-based plan prices, branch/terminal limits and support costs; do not treat example prices as final. |
| Performance | Benchmark on representative touchscreen PCs, tablets and local networks. |
| Privacy | Establish data retention, export/deletion policy and local regulatory compliance. |

## 17. Delivery milestones and quality gates
1. **PRD baseline:** Approve scope, workflows, acceptance scenarios and outstanding validations.
2. **Technical spike:** WPF/WebView2 React UI, .NET bridge, SQLite persistence, thermal print, two-terminal LAN gateway, restart/reconnect tests.
3. **Architecture specification:** Module boundaries, cloud/local authority, API contracts, security, synchronization and failure modes.
4. **Database design:** PostgreSQL schema, local SQLite/event schema, migrations, constraints and indexes.
5. **Claude Design:** Design system and approved operational screens; smooth, restrained animation.
6. **Claude Code foundation:** Monorepo, auth/tenant/branch isolation, CI, tests and deployment basics.
7. **Business modules:** Catalog, inventory, tables, orders, preparation, checkout, reporting.
8. **Offline/hardware:** Gateway, terminal enrollment, printer queue, sync and recovery.
9. **Commercialization:** Billing, entitlements, onboarding, installer, updates and support.
10. **Pilot and launch:** Real café-resto testing, security review, backup restore, fiscal validation and operational runbooks.

## 18. Claude collaboration rules
- `docs/01-PRD.md` defines **what** the product does; architecture/database/UI docs define **how**.
- Claude Design uses approved screens and UX rules; Claude Code implements approved designs, not obsolete explorations.
- Each coding prompt is short, scoped, testable and references relevant docs; avoid repeated full context.
- Do not expand MVP or change locked architecture without recording a decision and impact.
- Every completed module needs tests, acceptance evidence and a small reviewable commit.

---
**Next artifact:** `02-SYSTEM-ARCHITECTURE.md` (especially LAN gateway authority, offline synchronization and printer recovery).  
**PRD approval status:** Ready for review; regulatory/provider/hardware validations remain open and are not implied to be completed.
