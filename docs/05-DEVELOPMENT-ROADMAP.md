# SaaS POS — Development Roadmap

**Version:** 1.0  
**Date:** 2026-11-17  
**Status:** Implementation plan; technical assumptions require validation  
**First vertical:** Café-restaurants  
**Launch:** Tunisia first; international-ready foundations

## 1. Mission and non-negotiables

Deliver a commercial multi-tenant, multi-branch café-restaurant SaaS with a fast, polished Windows touchscreen POS, owner web dashboard, waiter mobile interface, customer QR ordering, kitchen display and SaaS administration. Prioritize correctness of money and orders, reliable printing, practical offline operation, and low-friction onboarding over feature count.

**Approved technology direction:** React + TypeScript + Vite + Tailwind + shadcn/ui + Motion; Windows .NET WPF host with WebView2; ASP.NET Core modular-monolith API; PostgreSQL cloud database; SQLite local persistence; .NET Windows Service LAN gateway; SignalR where useful. Use supported LTS .NET and pinned compatible dependency versions at implementation time. This is a design decision, not proof of hardware compatibility.

**Approved business decisions:** Monthly/annual entitlement-based subscriptions; optional assisted installation and hardware/setup packages; multiple branches and cash registers in MVP; waiter orders from mobile and POS; customer QR orders and additional submissions at an open table; configurable QR staff approval; kitchen/bar/counter printer routing and KDS; basic product stock, variants and extras; waiter-initiated card payments only through a validated compliant provider/terminal; cashier-confirmed cash payments; LAN offline with gateway, QR cloud-dependent in MVP.

## 2. Source of truth and change control

Documents: `01-PRD.md`, `02-SYSTEM-ARCHITECTURE.md`, `03-DATABASE-DESIGN.md`, `04-UI-UX-SPECIFICATION.md`, this roadmap, and `DECISIONS.md` (to be maintained). Treat existing docs as intended specifications; review them for contradictions before coding. Maintain a requirements-to-tests matrix and log unresolved assumptions. Do not silently change agreed behavior. Each milestone ends with a demo, test evidence, known issues, and an explicit go/no-go decision.

## 3. Delivery strategy

Use one repository with apps for web dashboards, waiter web, QR web, KDS, API, Windows POS host, and gateway, plus shared UI/types/contracts and infrastructure. Prefer a modular monolith to microservices. Keep the WPF shell thin: React owns presentation; C# owns native Windows integrations. Keep native bridge commands allowlisted, authenticated/authorized, validated, and auditable. Use server-side enforcement of tenant, branch, role, and entitlements. Never let the browser/client decide trusted prices, payment success, or permissions.

Development loop: **specify a small vertical slice → implement → automated tests → local manual test → review → commit → update documentation**. Claude Design produces approved boards and a design system; Claude Code implements only approved scope using short prompts and repository instructions. Use mocked external services until real integration contracts are verified.

## 4. Milestones, deliverables, exit gates

### M0 — Documentation baseline and risk register (1–3 working days, estimate)
- Reconcile PRD, architecture, database and UX documents; resolve contradictions and write `DECISIONS.md`.
- Build traceability table: requirement ID → implementation module → test → release status.
- Identify real target POS hardware, Windows versions, printers, payment provider candidates and Tunisian fiscal/legal questions.
- **Exit:** Approved MVP boundary, risks assigned, no critical undocumented assumption.

### M1 — Technical feasibility spikes (1–3 weeks, estimate)
Build disposable, minimal proofs—not the full app:
1. WPF + WebView2 loads a packaged React/Vite build without requiring a dev server; touch/keyboard navigation works.
2. Narrow, validated React↔C# bridge; app restart and crash recovery.
3. SQLite transactional sale draft and durable outbox; reconnect sync to a minimal ASP.NET Core API/PostgreSQL test service, including replay/idempotency.
4. Real target 80mm/58mm printer test, kitchen/bar routing and failure/unknown-print-result handling; cash drawer where applicable.
5. Two Windows POS devices + mobile waiter client through a designated LAN gateway with internet disconnected; restart gateway, disconnect one terminal, restore connectivity and reconcile.
6. Validate installer prerequisites (WebView2 runtime), signed release/update path and rollback; benchmark representative low-end POS hardware.
7. Investigate Tunisian card-payment initiation from waiter mobile; **do not claim mobile card acceptance until a provider and device flow are proven**.
- **Exit:** Evidence for each critical test, measured latency and recovery behavior, written decision to proceed or revise stack. If gateway fails, explicitly document allowed degraded operations; do not imply coordinated offline operation survives gateway failure.

### M2 — Architecture and security baseline (1–2 weeks, estimate)
- Monorepo/build/test/CI setup; local development via containers for API/PostgreSQL.
- Modular API boundaries, EF Core migrations, API contracts, validation, structured logging and health checks.
- Authentication, secure sessions, RBAC, branch-scoped authorization, tenant isolation (including negative cross-tenant tests), device enrollment model.
- Threat model: QR abuse, native bridge, offline entitlement tampering, printer replay, payment callbacks, sync conflicts and privileged actions.
- **Exit:** CI green; sample cross-tenant requests denied; secrets not committed; migration rollback/restore tested.

### M3 — UX system and approved design (1–3 weeks, overlaps M2)
- Claude Design: design tokens, typography, light/dark, FR-first with AR/RTL readiness, accessibility, touch targets and reduced motion.
- Approve core journeys: POS table grid → order → modifiers → send → pay; waiter mobile; QR menu; kitchen ticket; owner dashboard; setup/activation; offline/recovery states.
- Create shared React UI kit and interaction patterns; motion only when it improves clarity.
- **Exit:** Approved screens and interactive journeys on representative touchscreen and mobile sizes; no critical action depends on animation.

### M4 — Tenant, branch, identity, catalog (2–4 weeks, estimate)
- Businesses, branches, staff assignments, roles/permissions and branch-scoped settings.
- Product categories, products, variants, modifier groups, branch prices, availability and preparation-station routing.
- Basic stock movement ledger for tracked products; non-stock-tracked prepared food.
- **Exit:** Owner configures two branches, distinct prices/availability, restricted staff access; audit and validation tests pass.

### M5 — Restaurant order and preparation engine (3–5 weeks, estimate)
- Zones/tables/table visits; submissions and item-level states; repeat QR orders within a table visit.
- Staff direct dispatch; configurable QR approval; secure QR customer session showing only their own items.
- Kitchen/bar/counter routing, durable ticket outbox, KDS, printer queue, reprint/unknown outcome handling.
- Controlled post-dispatch edits/cancellations and station notifications.
- **Exit:** End-to-end tests for split station tickets, repeat submissions, QR approval, cancellation, printer disconnect/recovery and concurrent updates.

### M6 — Checkout, registers, reporting (2–4 weeks, estimate)
- Per-register cash sessions, opening/closing/reconciliation; cashier cash confirmation.
- Payment intents/records, partial and split payments, refunds/reversals, tax/price snapshots and immutable completed sale history.
- Waiter-initiated card flow behind a provider abstraction; enable live card acceptance only after provider certification/contract validation.
- Receipts, daily/branch/consolidated reporting; TND three-decimal monetary precision (decimal/integer minor units as specified, never floating point).
- **Exit:** No duplicate charge/sale under retries; totals reconcile across sessions, branches, refunds and reports; fiscal receipt requirements reviewed locally.

### M7 — LAN offline and synchronization hardening (3–6 weeks, estimate)
- Gateway service discovery and authenticated LAN clients; SQLite persistence; offline entitlements; local printing and KDS.
- Durable event/outbox sync, globally unique operation IDs, ordered financial mutations, idempotent cloud apply and conflict resolution rules.
- Define explicit behavior for gateway failure, LAN partition, simultaneous terminal edits, stock oversell and rejoining devices.
- QR cloud-only in MVP; clearly communicate outage behavior. Card transactions follow provider connectivity rules.
- **Exit:** Multi-terminal offline drills, gateway restart, duplicate replay, long outage, conflict and reconciliation tests pass with no missing/duplicated financial records.

### M8 — SaaS billing, installer and onboarding (2–4 weeks, estimate)
- Configurable entitlements for branches/terminals/modules; monthly/annual subscriptions; installation and hardware service line items.
- Verified payment webhook processing or manual verified activation; renewal reminders, controlled offline grace, historical export after expiry.
- Signed Windows installer, secure updates, terminal registration/revocation, guided setup and printer test.
- **Exit:** New business subscribes, activates two branches and terminals, completes first sale, renews/expires safely; license and device abuse tests pass.

### M9 — Production hardening and pilot (2–5 weeks, estimate)
- Security review and penetration testing, load/performance testing, accessibility and FR/AR checks, observability and support runbooks.
- Automated backups, restore rehearsal, retention/export policies, disaster recovery and incident response.
- Confirm Tunisian tax/invoicing/payment compliance with qualified local experts and providers.
- Pilot in one real café-resto, then expand to multiple branches; gather measured order times, printer failures, reconciliation discrepancies and staff feedback.
- **Exit:** Zero unresolved critical defects; operational acceptance and documented launch approval.

*All durations are rough planning estimates, not commitments. Parallelism depends on team size, hardware access and provider approvals.*

## 5. Release boundaries

**MVP includes:** Windows POS; web owner/admin; waiter mobile; customer QR with repeat submissions and configurable approval; table visits and item-level preparation; kitchen/bar/counter printers and KDS; multiple cash registers and branches; basic stock; variants/modifiers; cash and split checkout; card initiation subject to validated integration; offline LAN gateway; licensing, guided installation, security/audit and essential reporting.

**Later:** Ingredient/recipe inventory, advanced purchasing and stock transfers, delivery marketplaces, loyalty, international fiscal adapters, fully offline customer QR, complex floor-plan designer and additional retail verticals.

## 6. Cross-cutting acceptance and quality gates

- **Money:** Exact decimal calculations; stable price/tax snapshots; refunds and adjustments are traceable; duplicate request/retry cannot duplicate payment or sale.
- **Tenancy:** Every protected query and mutation enforces tenant and branch access; automated adversarial tests cover ID substitution.
- **Offline:** A confirmed sale survives process restart and reconnect; gateway outage behavior is explicit; sync is replay-safe and observable.
- **Printing:** Correct routing and quantities; no lost jobs; unknown outcomes surfaced for operator review rather than blind duplicate printing.
- **QR:** Unauthenticated users cannot view another customer's items or alter trusted prices; rate limits and revocation work.
- **Hardware:** Test actual target printer, drawer, touchscreen and low-spec PC before pilot.
- **Performance:** Define baseline targets during M1 and enforce them in M9; measure p95 touch-to-feedback, order dispatch, cold start, and reconnect recovery rather than inventing fixed guarantees.
- **UX:** French first, Arabic/RTL-ready, keyboard/touch accessible, reduced-motion supported, understandable error/offline states.
- **Operations:** Backups restored in rehearsal; secrets protected; signed releases; monitoring and alerting actionable.

## 7. Implementation sequencing and dependencies

M0 → M1 → M2; M3 can begin after M1 direction is validated and overlap M2. M4 precedes M5; M5 precedes M6; offline primitives are designed in M1/M2 and hardened in M7 (not bolted on at the end). M8 can partially overlap M6/M7. M9 begins only when critical end-to-end flows are testable.

Avoid building full admin dashboards before core transactional flows are proven. Implement one complete vertical slice early: **table → waiter order → station ticket → cashier cash payment → receipt → branch report → offline replay**.

## 8. Claude Design workflow

1. Give one compact prompt for design tokens and one core journey; attach approved UX specification.
2. Review desktop/touch/mobile states, loading/offline/errors and FR/RTL fit.
3. Lock accepted boards, tokens and component behavior; record decisions.
4. Export only approved screens/assets; exclude exploratory or obsolete variants.
5. Implement in React incrementally, verifying fidelity and responsiveness.

**Example prompt:**
> Design a premium, fast café-resto touchscreen POS: table grid, order composer, modifiers, kitchen status, cashier checkout and offline indicator. French-first, RTL-ready, 1366×768 and touch targets ≥44px. Clean enterprise aesthetic, subtle 120–180ms motion, no decorative delays. Provide key empty/error/loading states. Do not add unapproved features.

## 9. Claude Code workflow and prompt templates

Maintain concise `CLAUDE.md`: architecture, folder conventions, coding standards, approved scope, test commands, and rules against speculative rewrites. Use a separate issue/task for each small change. Require tests and a short completion report.

**M1 starter prompt:**
> Build only a feasibility spike: .NET WPF shell hosting packaged React/Vite via WebView2, one touchscreen product grid, minimal validated JS↔C# bridge and SQLite persistence of a sample sale. Include run/build steps and smoke tests. No authentication, subscriptions, cloud sync or production business modules yet. Report risks and hardware assumptions.

**Scoped feature prompt:**
> Implement [feature] per docs/01-PRD.md and docs/02-SYSTEM-ARCHITECTURE.md. Touch only [modules]. Enforce tenant/branch permissions, validation and idempotency where relevant. Add happy-path, denial and retry tests. No unrelated refactors. Report changed files, tests and open risks.

**Review prompt:**
> Review the changes for financial correctness, cross-tenant access, offline replay, error handling and spec drift. Fix only confirmed defects. Summarize evidence and unresolved risks.

## 10. Testing matrix (minimum)

| Scenario | Required evidence |
|---|---|
| Cross-tenant / cross-branch access | Denial tests for API and queries |
| Repeat QR submission | New items only; old tickets not resent |
| QR privacy | Customer sees only own items |
| Staff vs QR approval | Dispatch respects configured mode |
| Modifier/branch pricing | Correct snapshots and totals |
| Station routing | Bar/kitchen/counter receive correct tickets |
| Printer timeout | Durable pending/unknown state; controlled reprint |
| Concurrent checkout | Single finalization, no duplicate payments |
| Cash reconciliation | Opening/closing differences auditable |
| Refund | Immutable original + linked reversal |
| Multiple branches | Separate stock/sessions, consolidated reports |
| Internet outage | LAN waiter/POS/KDS operations continue |
| Gateway crash | Recovery and documented degraded behavior |
| Sync replay | Exactly-once business effect via idempotency |
| License expiry offline | Grace policy enforced and data accessible |
| Installer/update | Signed release, rollback and local data retained |
| Payment callback replay | No duplicate financial posting |
| Backup restore | Verified restore and consistency checks |

## 11. Risk register and external dependencies

| Risk | Mitigation / owner decision |
|---|---|
| Mobile card payments in Tunisia | Validate local acquiring/provider-supported terminal or hosted flow before promising live acceptance |
| Local fiscal and receipt requirements | Obtain Tunisia-specific professional review before commercial pilot |
| Printer driver/ESC-POS differences | Select target hardware and test physically in M1 |
| Gateway single point of failure | Recovery runbook, monitored service, safe degraded mode; assess standby/failover later |
| QR abuse/remote ordering | Rotatable QR IDs, rate limits, optional staff approval, session isolation |
| Offline sync conflicts | Defined authoritative rules, event log and reconciliation UI |
| WebView2 prerequisites | Installer packaging, runtime checks and supported Windows policy |
| Scope growth | Written MVP boundary and change-control process |

## 12. Definition of done for every task

- Requirement and acceptance criteria linked.
- Implementation matches approved architecture and UX.
- Automated tests pass; manual hardware test noted if required.
- Security and tenant/branch permissions reviewed.
- Logging, error states and rollback/migration implications addressed.
- Documentation updated and code committed with a focused message.
- No unapproved scope expansion or hidden unfinished work.

## 13. Immediate next actions

1. Review/reconcile the five specifications and establish `DECISIONS.md` and traceability list (M0).
2. Inventory one representative Windows touchscreen POS, printer(s), LAN setup and two terminals for M1.
3. Create a minimal feasibility repo and execute the M1 starter Claude Code prompt.
4. Record measured results and resolve blockers **before** full Claude Design or implementation.

**Next gate:** M0 documentation sign-off, followed by M1 .NET + React + WebView2 + SQLite + printer + LAN gateway proof.
