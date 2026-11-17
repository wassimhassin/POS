# SaaS POS — UI/UX Specification

**Document:** `04-UI-UX-SPECIFICATION.md`  
**Version:** 1.0 — design baseline  
**Date:** 2026-11-17  
**Status:** Ready for Claude Design exploration; visual concepts require explicit approval before implementation  
**Source of truth:** `01-PRD.md`, `02-SYSTEM-ARCHITECTURE.md`, `03-DATABASE-DESIGN.md`. If this document conflicts with operational, security or data-integrity rules in those documents, those rules prevail.

## 1. Experience vision

Create a premium, contemporary, calm and exceptionally efficient café-restaurant POS. It should look polished without feeling decorative or slowing staff down. The main product differentiators are (1) a coherent, elegant interface and (2) fast, dependable workflows with clear status and recovery guidance.

**Design principles:**
1. **Fast first:** frequent actions require few interactions, predictable focus and no unnecessary transitions.
2. **Clarity under pressure:** table state, order state, payment state, kitchen state and connectivity state are visually distinct.
3. **Touch-first, not touch-only:** large touch targets, mouse support and keyboard shortcuts on Windows.
4. **One language of design:** shared components and tokens across desktop, dashboard, mobile, QR and kitchen; distinct task-appropriate layouts.
5. **Safe by default:** destructive and financial actions communicate consequences, permission requirements and outcome.
6. **Offline is a first-class state:** show what works locally, what is queued and what is unavailable; never pretend that cloud synchronization has completed.
7. **Accessible and international-ready:** French-first, Arabic/RTL-ready, TND with three fractional digits, locale-aware formats; avoid hard-coded text.

## 2. Product surfaces and navigation models

| Surface | Main device | Navigation | Primary goal |
|---|---|---|---|
| Windows POS | Touchscreen Windows terminal, mouse/keyboard | Persistent primary rail + contextual workspace; minimal nesting | Table/order/checkout speed |
| Waiter mobile | Smartphone/tablet browser | Compact top bar, bottom primary navigation, sticky actions | Take/send orders at table, initiate card payment |
| Customer QR | Mobile browser, no install/login | Category browsing, product detail, cart, own orders | Fast, understandable self-ordering |
| Kitchen display (KDS) | Touchscreen or monitor | Station-specific queue with clear status columns | Prepare items in sequence |
| Owner/manager dashboard | Desktop/tablet browser | Left sidebar + branch selector + contextual filters | Manage branches and analyze operations |
| SaaS super-admin | Desktop browser | Restricted admin navigation, search and audit tools | Subscription/device/support administration |
| Setup/activation | Windows desktop + web | Guided wizard with progress, test and retry | Get business operational without technical assistance |

The desktop shell is **.NET WPF + WebView2**; React + TypeScript renders the main UI. Native capabilities (printer, local storage, device services) must be exposed through a minimal, versioned and permission-checked bridge. Avoid duplicate WPF business UI.

## 3. Visual design direction and token system

**Direction:** refined enterprise hospitality software: restrained surfaces, high legibility, elegant spacing, subtle depth and strong visual hierarchy. Do not copy a competitor's branding or screen composition.

Design in Figma/Claude Design using named semantic tokens rather than fixed per-screen colors:

- `surface/base`, `surface/raised`, `surface/overlay`, `surface/selected`
- `text/primary`, `text/secondary`, `text/muted`, `text/inverse`
- `border/default`, `border/focus`
- `action/primary`, `action/hover`, `action/disabled`
- `status/success`, `status/warning`, `status/danger`, `status/info`, `status/offline`
- `station/kitchen`, `station/bar`, `station/counter` (never color alone)
- `space/1..8`, `radius/sm|md|lg`, `shadow/low|medium`

**Typography:** choose a licensed, broadly available sans-serif supporting French and Arabic; test Arabic glyphs, RTL numbers and mixed-direction content. Prefer 16px minimum body text on mobile; POS core values and totals larger. Use tabular numerals for monetary figures. Design both light and dark modes; initial pilot can ship with a single fully validated theme if schedule demands.

**Density:** Windows POS can be information-dense but must retain comfortable touch targets. Default interactive targets **at least 44×44 CSS px**, preferably 48×48 or larger for high-frequency POS actions. Maintain adequate contrast (target WCAG 2.2 AA for applicable web interfaces) and visible keyboard focus.

**Icons:** use consistent outlined/filled icon rules; pair ambiguous icons with labels. Never rely solely on color for payment, table, preparation or sync status.

## 4. Motion and interaction performance

Use Motion sparingly for React transitions; prefer CSS transitions for simple feedback. Animation should confirm state, not delay task completion.

- Typical micro-interactions: **100–180 ms**; panels/modals: **150–250 ms**; no prolonged choreography on checkout.
- Honor `prefers-reduced-motion`; use nonanimated alternatives.
- Avoid animating layout for entire product grids or order lists; use transform/opacity where possible.
- Never block an action until an animation finishes; disable repeated submissions while a command is pending.
- Preserve scroll, search and order draft context when switching views.
- Target responsive tap feedback (<100 ms perceptual target) and smooth interaction on representative low/mid-range POS hardware. These are **validation targets, not measured guarantees**.
- Loading: skeleton for content fetches, concise inline progress for commands, visible durable success/pending/error states.

## 5. Windows POS — information architecture

### 5.1 Global shell

Persistent elements: branch and register identity, employee identity, current cash session, network/gateway/sync indicator, time, accessible help, notifications and controlled lock/logout. Avoid exposing administrative settings in the primary sale flow.

Primary navigation: **Tables**, **Quick Sale**, **Orders**, **Payments/Requests**, **Cash Session**, **More** (role-gated). A single action should return to the active table/order without losing unsent draft items.

### 5.2 Table overview

- Branch > zone selector (Salle, Terrasse, VIP etc.) > responsive table grid.
- Each card: table number/name, status label, visit duration, order amount if authorized, waiter assignment, alert badge when relevant.
- States: available, occupied, reserved, needs cleaning, disabled; reservation status must not be confused with active order state.
- Quick actions: open visit, assign waiter, transfer table (permission checked), request bill, mark cleaning complete.
- Search/filter by table, zone, status; no drag-and-drop floor planner required in MVP.
- When two terminals act on the same visit, show current state and resolve conflicts using server/gateway versions rather than overwriting silently.

### 5.3 Order workspace

Recommended desktop layout: left category/search and product grid; right persistent order panel with table, customer notes, line items, modifiers, quantity controls, subtotal, discounts/tax/total and prominent **Send to preparation** action. For smaller displays, collapse secondary navigation but keep order summary reachable.

- Product cards: name, variant starting price, availability, optional image; do not require images for speed.
- Variant/modifier sheet: size, required choices, min/max modifier constraints, extras, free-text note; validate before add.
- Distinguish **unsent draft** from **submitted/printed** items.
- Additional orders append a new submission to the same table visit; never resend old items.
- Dispatched item changes create explicit modification/cancellation requests with permission, reason and station notification.
- Show per-line preparation state without implying that the entire visit shares one kitchen state.
- When item unavailable or price changes, show an actionable explanation and preserve unaffected draft lines.

### 5.4 Checkout and payment

- Prominent due amount in TND formatted as `0.000 TND`; break down subtotal, discounts, taxes, tips, paid and remaining.
- Support cash at cashier, card via validated payment provider, split tenders and item-based split bills.
- Payment states: not started, initiated, awaiting provider, confirmed, failed, canceled, uncertain/requires reconciliation. **Never mark paid based on an unverified client response.**
- Cashier-only confirmation of cash receipt; waiter mobile can initiate card payment but cannot confirm cash.
- Refunds, reversals and discounts require explicit authorization/reason as configured.
- Disable duplicate submission; provide a safe lookup/reconcile path after timeout.
- Receipt actions: print, reprint with audit, view receipt; do not silently close a visit until its balance and unresolved items are reconciled.

### 5.5 Cash sessions

Opening amount, cashier/register, timestamp, cash in/out (reason required), expected cash, counted cash, discrepancy and closing confirmation. Display a read-only summary after closing. Warn when another session or payment operation makes closure unsafe.

## 6. Waiter mobile experience

**Navigation:** Tables, My Orders, Requests, Profile/Status. Branch-scoped and role-gated.

Core journey: login → choose assigned branch/zone → select table → add products/modifiers → review → send directly to preparation → observe station updates → add more items as a new submission → request cashier cash payment **or** initiate supported card payment via certified provider flow.

Requirements:
- One-hand-friendly sticky bottom primary action, readable cards and concise modals.
- Visible local Wi-Fi/gateway status; when offline from cloud but connected to gateway, show **Local mode**.
- When disconnected from both gateway and cloud, disable shared write operations unless a specifically tested fallback is implemented; do not promise offline multi-device coordination.
- Never capture/store raw payment card details in the waiter UI.
- Confirm that a ticket was accepted into the durable queue; distinguish this from physically printed.

## 7. Customer QR ordering

**Entry:** scan table QR → branch/table context → menu → product detail and modifiers → cart → review → submit → own order status → add items while table visit is open.

- No account or installation required; issue a secure customer session scoped to their own submissions.
- Show only **their own submitted items**. Never reveal the entire table bill without separate verification.
- Two configurable modes: **staff approval** (pilot default) and **direct dispatch**. Label the resulting status accurately: `Awaiting approval`, `Accepted`, `Preparing`, `Ready` etc.
- Server verifies product availability, prices, allowed modifiers, table validity and duplicate submission protection.
- A QR scan alone is not proof of physical presence; provide staff moderation, rate limiting, rotation/revocation and abuse controls.
- QR ordering is **internet-dependent in MVP**; display a friendly offline/unavailable message rather than a misleading submission confirmation.
- Customer payment online is **not** MVP; indicate payment through waiter/cashier.
- Accessible French-first copy, Arabic/RTL-ready layout and explicit allergen information only where verified business data is provided.

## 8. Kitchen display and printing UI

KDS is station-scoped. Queue cards show ticket ID, table, source (POS/waiter/QR), submission time, elapsed time, items/quantities, modifiers, notes and state.

- Default columns: **New → Preparing → Ready**; optional served acknowledgment by staff outside kitchen.
- High-contrast, large touch targets, optional sound/visual alerts (configurable), clear aging indicators.
- New submission and cancellation/modification are unmistakably differentiated; do not erase original dispatch history.
- Filter by station; kitchen, bar and counter receive only relevant items.
- Printing status is distinct from preparation status: queued, sent, confirmed where supported, failed, **unknown outcome**. Unknown print outcomes require human review; do not blindly reprint.
- Offline gateway maintains a durable queue; KDS indicates local/cloud status and recovery needs.

## 9. Owner/manager web dashboard

**Navigation groups:** Overview, Operations, Catalog, Inventory, Staff, Branches, Reports, Settings, Subscription. Global branch filter supports **All branches** for authorized owners and branch-specific views for managers.

### Priority screens

1. Overview: sales, orders, payment-method breakdown, refunds, register discrepancies and branch comparison; label reporting freshness and local pending sync.
2. Branch management: branch info, hours, zones, stations, registers, devices, printers and permissions.
3. Catalog: products, categories, variants, modifier groups, availability, branch-specific prices, preparation station mapping.
4. Inventory: stock levels and movement history; basic product stock only (ingredient/recipe deductions excluded from MVP).
5. Staff: invite, role assignment, branch scope, deactivation and audit.
6. Reports: date range, branch scope, gross/discount/refund/tax/net breakdown, export controls.
7. Settings: tax and receipt templates, language, approval mode, print routing, safe configuration testing.
8. Subscription: billing cycle, entitlements, branch/terminal usage, renewal and invoices, optional installation/hardware services.

Show empty states with clear next actions. Destructive branch/device operations require explicit confirmation and impact summary.

## 10. SaaS super-admin

Separate privileged interface, not a hidden menu in the merchant dashboard. Core views: tenant search, subscriptions and entitlements, billing events, device activations/revocations, assisted setup tickets, service health and audit logs. Use explicit impersonation/support-access approval and traceability if support access is later introduced; no silent cross-tenant access.

## 11. Onboarding and installation experience

**Self-service path:** website registration → plan/payment verification → business creation → branch setup → installer download → install WebView2 prerequisites if needed → secure login/device registration → assign branch/register → printer discovery/test → first products/import → cash session → guided first test sale.

Wizard should show steps, progress, clear error messages and resume capability. Explain local gateway host selection and backup/availability responsibility in plain language. Offer assisted setup as a separate paid option; do not make self-service dependent on support.

## 12. Global states, error handling and recovery

All operational screens must specify **loading, empty, success, validation error, permission denied, disconnected, pending sync, conflict and retry** states.

**Status banner semantics:**
- `Online / Synced`: backend acknowledged and no known pending operations.
- `Local mode / Pending sync`: gateway reachable, cloud unavailable; local writes can continue for supported operations.
- `Gateway unavailable`: explain what is blocked and how to recover; do not imply multi-terminal consistency.
- `Payment pending verification`: never imply a charge failed or succeeded until confirmed.
- `Printer needs attention`: identify station, printer and ticket; offer authorized reprint/reconciliation.

Use human-readable messages with a concise action; log technical details privately. Never expose secrets, raw payment data or other tenants' information.

## 13. Responsive and accessibility requirements

- Design reference canvases: POS **1366×768**, **1280×800** and **1920×1080**; waiter **390×844** and **768×1024**; QR **360×800**; dashboard **1440×900**; KDS **1920×1080**. Validate actual target hardware and scaling (100%, 125%, 150%).
- Avoid horizontal scrolling in primary POS and checkout flows; allow structured data tables to scroll where appropriate.
- Support keyboard focus, tab order, Enter/Escape semantics, accessible labels, text scaling and contrast.
- French is initial primary language; design RTL variants for Arabic, including mirrored navigation where appropriate and correct mixed-direction amounts/product codes.
- Currency uses decimal-safe backend calculations; UI formats values and never performs authoritative financial math.
- Critical actions remain usable with images disabled and low-bandwidth conditions.

## 14. Component library and handoff contract

**Shared components:** AppShell, BranchSelector, StatusBadge, ConnectivityIndicator, ProductCard, VariantPicker, ModifierGroup, OrderLine, OrderSummary, MoneyDisplay, TableCard, TicketCard, PaymentMethodSelector, ConfirmActionDialog, PermissionGate, Toast, EmptyState, Skeleton, ErrorState, PrinterStatus, SyncQueueBadge, FormField, DataTable, DateRangeFilter.

For every approved Claude Design screen, capture:
- Screen ID and route/surface, role/branch visibility and primary use case.
- Component hierarchy and semantic tokens; desktop/mobile breakpoints.
- All states and transitions; error, offline, loading and empty variants.
- Interaction annotations (tap targets, keyboard shortcuts, focus, modals).
- Backend actions and data required; permissions and audit implications.
- Acceptance checklist and reference screenshot/export.

Do **not** treat visual mockups as authorization to change PRD business rules. Approved boards are the visual source of truth; obsolete alternatives are excluded from implementation.

## 15. Design production order and Claude Design workflow

Design in short, focused batches rather than one giant prompt:

1. **Foundation:** visual direction, tokens, typography, navigation shells and states.
2. **Windows POS core:** table grid → order workspace → variants/modifiers → checkout/cash session.
3. **Restaurant operations:** kitchen display, print errors, cancellations and conflict handling.
4. **Waiter mobile:** tables, ordering, card initiation, local mode.
5. **Customer QR:** menu, modifiers, cart, approval status, own-order history.
6. **Business dashboard:** overview, catalog, branches, staff, stock, reports.
7. **SaaS super-admin and onboarding:** plans, activation, devices, guided setup.
8. **Responsive, RTL, accessibility and motion audit.**

**Example concise first Claude Design prompt (use after technical feasibility gate):**

> Design a premium, fast café-resto Windows POS visual system for React/WebView2. French-first, Arabic/RTL-ready, touch-first (48px targets), restrained motion, clear offline and payment states. Create design tokens, POS shell, table grid and order workspace at 1366×768 and 1280×800, including loading/empty/error variants. No backend or implementation; do not invent business features. Deliver annotated, consistent screens for approval.

## 16. Acceptance criteria and quality gates

The design is **not approved** until the following are demonstrable in prototypes and annotated handoff:

1. A waiter/cashier can open a table, add a variant with extras and send a new submission without confusing old tickets.
2. A cashier can identify the due amount, take cash, and complete a sale with clear success/reconciliation states.
3. Waiter mobile can initiate card payment without handling raw card details and can clearly see pending vs verified status.
4. QR customer can add more items, see only their own orders, and understand staff approval vs direct dispatch.
5. Kitchen staff can distinguish new, modified, canceled and unknown-print-outcome tickets.
6. Owner can switch branches and compare consolidated vs per-branch reports without misleading totals.
7. Offline/local mode, gateway failure and pending synchronization are visibly and accurately differentiated.
8. Core POS screens are usable at 1280×800 with Windows display scaling and no obstructed primary action.
9. Keyboard, touch, French and RTL layouts are tested; reduced-motion preference is respected.
10. Motion never blocks checkout, and design meets measured responsiveness targets on representative hardware.
11. Security-sensitive actions expose permissions and confirmation, never merely rely on hidden buttons.
12. Every screen has a documented empty/loading/error/permission state and named components.

## 17. Scope exclusions and open validation items

**Not in MVP:** drag-and-drop floor-plan designer, loyalty program, delivery integrations, ingredient/recipe inventory, online customer payment, native waiter app, and offline customer QR ordering.

**Must validate before implementation commitment:** real POS screen sizes and Windows scaling; thermal printer and cash drawer hardware; local gateway behavior and WebView2 startup; supported Tunisian card-payment provider/terminal workflow; fiscal receipt/invoice obligations; Arabic font/RTL usability; performance on representative devices. No visual design can substitute for these technical or regulatory checks.

## 18. Next deliverables

1. `05-DEVELOPMENT-ROADMAP.md` with scoped milestones, acceptance gates and concise Claude prompts.
2. Phase 1 hardware/technical feasibility prototype for WPF/WebView2, SQLite, local gateway, printing and recovery.
3. Claude Design concept boards only after technical constraints are known; approve the visual baseline before production frontend work.

---

**Change control:** Any requested feature that changes money, order lifecycle, tenant/branch access, printer routing or offline guarantees requires an explicit update to the PRD/architecture/database specification before UI implementation.
