# SaaS POS — Database Design

**Document:** `03-DATABASE-DESIGN.md`  
**Version:** 1.0 — architecture-level logical schema  
**Status:** Proposed baseline for technical validation; not executable migrations  
**Date:** 2026-11-17  
**Depends on:** `01-PRD.md`, `02-SYSTEM-ARCHITECTURE.md`

## 1. Purpose and non-negotiable principles

Design the data foundation for a Tunisia-first, multi-tenant, multi-branch café-restaurant SaaS with Windows POS, waiter mobile, QR ordering, kitchen/bar/counter routing, multiple cash registers, basic stock, offline LAN gateway, and subscriptions. Other retail verticals and countries must be addable without replacing the core schema.

1. **Cloud PostgreSQL** holds canonical tenant configuration, accepted branch events, financial history, reporting projections, subscriptions and platform administration.
2. **One designated branch gateway SQLite database** is the durable authoritative operational writer for that branch while processing POS/waiter commands (online and offline). The cloud accepts synchronized events and provides canonical reporting/history. Cloud-origin changes to active branch state require versioning/serialization to prevent dual writers.
3. **Terminal SQLite** is a protected local cache and device state store, *not* an independent writer of shared table/order state while the gateway is healthy. Emergency standalone sale queue is **not approved** for MVP until separately designed and tested.
4. Tenant and branch boundaries must be enforced at the API, domain layer and database, including composite foreign keys and tested authorization policies.
5. Financial records are append-only after finalization. Use reversal/refund/adjustment entries, never destructive edits.
6. Every accepted command and external callback is idempotent; durable outbox/inbox patterns govern sync and printing.
7. No raw card numbers, CVV, payment-provider secrets, or signing private keys in the frontend, terminal cache or business database.

## 2. Conventions, types and identifiers

| Convention | Rule |
|---|---|
| IDs | UUID for globally unique entity/operation IDs; generated offline safely. UUIDv7 may be used after library support is validated. |
| Tenant scope | Tenant-owned rows have `tenant_id NOT NULL`; branch-owned rows additionally have `branch_id NOT NULL`. |
| Timestamps | `timestamptz` UTC in PostgreSQL; UTC ISO-8601 or integer epoch in SQLite; branch IANA timezone for display/report grouping. |
| Money | Signed 64-bit integer **minor units** plus ISO currency code. TND uses 1/1000 TND; no floats for prices or settlement. |
| Quantity | `numeric(18,3)` for countable stock/weighted quantities, with unit of measure; explicit rounding. |
| Version | Integer `version` incremented on state transitions, checked with optimistic concurrency. |
| Status | Validated string/enum plus domain transition rules; do not rely only on enum membership. |
| Soft deletion | `archived_at` for catalog/configuration where historical references exist; no cascading deletion of financial history. |
| Audit | `created_at`, `updated_at`, actor identifiers where applicable; privileged changes also produce audit events. |
| Naming | `snake_case`, plural table names, singular `_id` foreign keys; explicit constraint/index names in migrations. |

**PostgreSQL monetary rule:** `amount_minor bigint NOT NULL`, `currency_code char(3) NOT NULL`; all sums checked for overflow, taxes/discounts calculated using documented rounding rules. A payment amount and its allocations must use compatible currencies; currency conversion is outside MVP.

## 3. Logical domains and ownership

```text
PLATFORM
  tenants ── tenant_memberships ── users
     │
     ├── branches ── branch_staff ── terminals ── registers ── cash_sessions
     │      ├── zones ── dining_tables ── table_visits ── order_submissions
     │      │                                      ├── order_lines ── line_modifiers
     │      │                                      └── preparation_tickets ── ticket_lines
     │      ├── stations ── printer_devices ── print_jobs
     │      ├── branch_products ── stock_locations ── stock_movements
     │      └── branch_operations ── sync_checkpoints
     ├── products ── product_variants ── modifier_groups ── modifier_options
     ├── subscriptions ── subscription_entitlements
     └── sales ── sale_lines ── payment_allocations ── payments ── refunds
```

The diagram shows ownership, not all cardinalities or constraints. Detailed relationships follow.

## 4. Cloud PostgreSQL schema — platform, identity and access

| Table | Key fields / relationships | Key constraints |
|---|---|---|
| `tenants` | `id`, `legal_name`, `display_name`, `default_currency`, `country_code`, `status` | Tenant status transitions controlled; no hard delete with records. |
| `branches` | `id`, `tenant_id`, `name`, `timezone`, `country_code`, `currency_code`, `status` | `UNIQUE(tenant_id,id)`; unique branch code within tenant. |
| `users` | `id`, identity provider subject, normalized email/phone, security metadata | Authentication secrets managed by ASP.NET Core Identity; no plaintext credentials. |
| `tenant_memberships` | `id`, `tenant_id`, `user_id`, `status` | `UNIQUE(tenant_id,user_id)`; user may belong to multiple tenants explicitly. |
| `roles` | `id`, `tenant_id NULL` for platform templates, `name`, `is_system` | Tenant-specific names unique; immutable system permissions. |
| `permissions` | `code`, `description` | Globally unique permission code. |
| `role_permissions` | `role_id`, `permission_code` | Unique pair; foreign keys. |
| `staff_assignments` | `tenant_id`, `branch_id`, `membership_id`, `role_id`, `status` | Composite FKs guarantee same tenant/branch; support several roles/branches. |
| `device_registrations` | `id`, `tenant_id`, `branch_id`, `device_public_key`, `device_type`, `revoked_at`, `last_seen_at` | Unique registered public-key fingerprint; no reusable plaintext activation secrets. |
| `audit_events` | `id`, `tenant_id`, `branch_id NULL`, `actor_id`, `action`, `target_type`, `target_id`, `before_after_redacted`, `occurred_at` | Append-only; redact sensitive values; retention policy. |

Platform super-admin identity and operations must use **separate authorization boundaries**; tenant roles never imply platform-level access. For all branch-owned tables use composite FKs such as `(tenant_id,branch_id) REFERENCES branches(tenant_id,id)` and for child references use `(tenant_id,branch_id,parent_id)` referencing an appropriate unique key on the parent.

## 5. Catalog, variants, modifiers and branch configuration

| Table | Key fields | Rules |
|---|---|---|
| `categories` | `id`, `tenant_id`, `parent_id NULL`, `name`, `sort_order`, `archived_at` | Parent within same tenant. |
| `products` | `id`, `tenant_id`, `category_id`, `name`, `description`, `tax_category_id`, `track_stock`, `archived_at` | Tenant-wide shared catalog. |
| `product_variants` | `id`, `tenant_id`, `product_id`, `label`, `sku`, `base_price_minor`, `currency_code`, `track_stock` | Unique SKU per tenant where present; distinct price per variant. |
| `modifier_groups` | `id`, `tenant_id`, `name`, `min_select`, `max_select`, `allow_repeat` | `0 <= min_select <= max_select`. |
| `modifier_options` | `id`, `tenant_id`, `group_id`, `name`, `price_delta_minor`, `archived_at` | Group/option same tenant. |
| `product_modifier_groups` | `tenant_id`, `product_id`, `group_id`, `sort_order` | Unique pair; enforce allowed modifiers. |
| `branch_products` | `tenant_id`, `branch_id`, `product_id`, `available`, `station_id NULL` | One row per branch/product; default fallback rules explicit. |
| `branch_variant_prices` | `tenant_id`, `branch_id`, `variant_id`, `price_minor`, `currency_code` | Branch-specific override; no cross-currency price fallback. |
| `tax_categories` | `id`, `tenant_id`, `code`, `name` | Tax treatment must be validated locally. |
| `tax_rules` | `id`, `tenant_id`, `country_code`, `effective_from`, `effective_to`, `rate_basis_points_or_rational`, `rounding_policy` | Time-bounded effective dating; tax snapshots on sales. |

**Price resolution:** branch override → tenant variant price; apply selected modifiers, discounts and tax policy server/gateway-side. Persist product/variant/modifier labels, price, tax and configuration versions on order/sale lines so later catalog edits cannot rewrite history. Product station routing may be overridden per branch; validate station belongs to that branch.

## 6. Tables, visits and orders

| Table | Key fields | Rules |
|---|---|---|
| `zones` | `id`, `tenant_id`, `branch_id`, `name`, `sort_order` | Unique name within branch where active. |
| `dining_tables` | `id`, `tenant_id`, `branch_id`, `zone_id`, `label`, `enabled` | Unique label within branch; zone same branch. |
| `table_visits` | `id`, `tenant_id`, `branch_id`, `table_id`, `opened_at`, `closed_at`, `status`, `version` | Partial unique index on `table_id` for active visits (scoped tenant/branch); close only after settlement. |
| `guest_order_sessions` | `id`, `tenant_id`, `branch_id`, `visit_id`, `token_hash`, `expires_at`, `revoked_at` | Private QR guest session, not full-table read authorization. |
| `order_submissions` | `id`, `tenant_id`, `branch_id`, `visit_id`, `origin`, `submitted_by`, `guest_session_id NULL`, `approval_status`, `submitted_at`, `version` | Origin `pos/waiter/qr`; QR approval policy snapshot. |
| `order_lines` | `id`, `tenant_id`, `branch_id`, `submission_id`, `variant_id`, `quantity`, `unit_price_minor`, `tax_snapshot`, `notes`, `preparation_status`, `version` | Nonnegative/valid quantities; snapshots; changes after dispatch append corrections. |
| `order_line_modifiers` | `id`, `tenant_id`, `branch_id`, `line_id`, `option_id`, `name_snapshot`, `price_delta_minor`, `quantity` | Validate allowed selections at submission. |
| `order_adjustments` | `id`, `tenant_id`, `branch_id`, `line_id`, `kind`, `quantity_delta`, `amount_delta_minor`, `reason`, `approved_by`, `created_at` | Append-only; post-dispatch corrections create station notices. |
| `qr_table_tokens` | `id`, `tenant_id`, `branch_id`, `table_id`, `token_hash`, `rotated_at`, `revoked_at` | High-entropy opaque tokens, hashed at rest, rate-limited. |

**State separation:** table visit status, submission approval, line preparation, print/KDS delivery, sale settlement are distinct. A new submission joins the existing open visit; do not resend prior lines. Concurrency: expected `table_visits.version` and unique command ID. QR guests see only submissions tied to their authenticated guest session; table token alone grants no other guest's order history.

## 7. Preparation stations, KDS and physical printing

| Table | Key fields | Rules |
|---|---|---|
| `preparation_stations` | `id`, `tenant_id`, `branch_id`, `name`, `type`, `active` | Kitchen/bar/counter configurable per branch. |
| `station_routes` | `tenant_id`, `branch_id`, `product_id/variant_id`, `station_id`, `priority` | Resolve routing deterministically; same branch. |
| `preparation_tickets` | `id`, `tenant_id`, `branch_id`, `submission_id`, `station_id`, `kind`, `created_at`, `status` | One submission may create several tickets; corrections use new tickets. |
| `preparation_ticket_lines` | `id`, `tenant_id`, `branch_id`, `ticket_id`, `order_line_id`, `quantity`, `display_snapshot` | Unique association per ticket/line as appropriate. |
| `printer_devices` | `id`, `tenant_id`, `branch_id`, `station_id NULL`, `device_name`, `connection_config_encrypted`, `active` | Printer secrets/config protected; never in browser. |
| `station_outputs` | `id`, `tenant_id`, `branch_id`, `station_id`, `output_type`, `target_id` | Printer, KDS or both. |
| `print_jobs` | `id`, `tenant_id`, `branch_id`, `ticket_id`, `printer_id`, `payload_hash`, `status`, `attempts`, `last_error`, `created_at` | Unique deduplication key per ticket/printer/job kind; durable queue. |
| `print_attempts` | `id`, `tenant_id`, `branch_id`, `job_id`, `started_at`, `outcome`, `operator_id NULL` | Ambiguous physical outcomes explicitly recorded. |
| `kds_events` | `id`, `tenant_id`, `branch_id`, `ticket_id`, `station_id`, `event_type`, `actor_id`, `occurred_at` | Append-only preparation history. |

**Printing rule:** `sent_to_driver` is not `physically_printed`. When the printer cannot confirm physical output, mark `unknown_requires_review`; staff may perform an audited reprint. No unbounded automatic retries for ambiguous outcomes.

## 8. Sales, payments, refunds and cash sessions

| Table | Key fields | Rules |
|---|---|---|
| `sales` | `id`, `tenant_id`, `branch_id`, `visit_id NULL`, `sale_number`, `status`, `currency_code`, `subtotal_minor`, `discount_minor`, `tax_minor`, `total_minor`, `finalized_at` | Unique `(tenant_id,branch_id,sale_number)`; finalized immutable; fiscal numbering subject to legal review. |
| `sale_lines` | `id`, `tenant_id`, `branch_id`, `sale_id`, `order_line_id NULL`, `description_snapshot`, `quantity`, `unit_price_minor`, `tax_minor`, `line_total_minor` | Snapshots immutable after finalization. |
| `payment_intents` | `id`, `tenant_id`, `branch_id`, `provider`, `provider_reference`, `idempotency_key`, `amount_minor`, `status` | Unique provider/idempotency references; card flow initiated, not assumed paid. |
| `payments` | `id`, `tenant_id`, `branch_id`, `method`, `amount_minor`, `currency_code`, `provider_reference NULL`, `cash_session_id NULL`, `confirmed_at`, `status` | Card requires trusted verification; cash requires authorized cashier/session. |
| `payment_allocations` | `id`, `tenant_id`, `branch_id`, `payment_id`, `sale_id`, `amount_minor` | Sum allocations <= confirmed payment; prevent overpayment by transactional locks. |
| `refunds` | `id`, `tenant_id`, `branch_id`, `payment_id`, `amount_minor`, `reason`, `approved_by`, `provider_ref NULL`, `status` | Separate append-only records; cap against refundable balance. |
| `cash_registers` | `id`, `tenant_id`, `branch_id`, `name`, `terminal_id NULL` | Terminal association controlled; register per branch. |
| `cash_sessions` | `id`, `tenant_id`, `branch_id`, `register_id`, `cashier_membership_id`, `opening_float_minor`, `opened_at`, `closed_at`, `counted_minor`, `expected_minor`, `difference_minor`, `status` | Partial unique index: at most one open session per register. |
| `cash_movements` | `id`, `tenant_id`, `branch_id`, `cash_session_id`, `kind`, `amount_minor`, `reason`, `actor_id`, `occurred_at` | Append-only; linked to authorized session. |
| `receipt_issues` | `id`, `tenant_id`, `branch_id`, `sale_id`, `issue_kind`, `sequence_no`, `issued_at`, `actor_id` | Reprints traceable; fiscal formatting/sequence to validate. |

**Payment invariants:** `confirmed card payment` only after authenticated provider result or reconciled server query; webhook event IDs unique; duplicate callbacks no-op. A table visit closes only after chargeable items are resolved and net outstanding is zero. Payment/refund records are not deleted. Split-bill allocation is transactionally consistent and prevents double allocation of a line or balance.

**Card-provider dependency:** Exact Tunisia-compatible mobile card payment model is unconfirmed. Do not fabricate provider schema or implement raw-card handling. Define adapter tables after provider certification/contract validation.

## 9. Inventory and reporting

| Table | Key fields | Rules |
|---|---|---|
| `stock_locations` | `id`, `tenant_id`, `branch_id`, `name` | Branch-scoped. |
| `stock_movements` | `id`, `tenant_id`, `branch_id`, `location_id`, `variant_id`, `quantity_delta`, `unit`, `reason`, `source_type`, `source_id`, `operation_id`, `occurred_at` | Append-only; unique source operation per movement type; no silent quantity edits. |
| `stock_balances` | `tenant_id`, `branch_id`, `location_id`, `variant_id`, `quantity`, `version` | Derived transactional projection; reconcile against movements. |
| `report_daily_branch_sales` | `tenant_id`, `branch_id`, `business_date`, monetary aggregates, `last_applied_event_position` | Derived, rebuildable, no authoritative financial truth. |

MVP: countable product/variant stock and adjustments; optional oversell policy per branch. Prepared dishes may be non-stock-tracked. Ingredient recipes, procurement automation and inter-branch stock transfers are out of MVP. Use branch-local business date and timezone, including daylight-saving edge cases where applicable.

## 10. Subscriptions, entitlements and onboarding

| Table | Key fields | Rules |
|---|---|---|
| `plans` | `id`, `code`, `name`, `active` | Plan is commercial configuration, not hard-coded permission logic. |
| `plan_prices` | `id`, `plan_id`, `currency_code`, `billing_interval`, `amount_minor`, `effective_from` | Monthly/annual, effective dated. |
| `entitlement_definitions` | `code`, `value_type`, `description` | E.g. `max_branches`, `max_terminals`, `qr_ordering_enabled`. |
| `plan_entitlements` | `plan_id`, `entitlement_code`, `value_json` | Validate value against definition. |
| `subscriptions` | `id`, `tenant_id`, `plan_id`, `status`, `period_start`, `period_end`, `grace_until`, `provider_ref NULL` | Explicit lifecycle and renewal history. |
| `subscription_entitlements` | `id`, `tenant_id`, `subscription_id`, `code`, `value_json`, `valid_from`, `valid_until` | Resolved server-authoritative snapshot. |
| `entitlement_issuances` | `id`, `tenant_id`, `branch_id NULL`, `device_id NULL`, `issued_at`, `expires_at`, `key_id`, `claims_hash` | Signed tokens generated by protected server key, never database-stored private key. |
| `billing_invoices` | `id`, `tenant_id`, `currency_code`, `amount_minor`, `status`, `issued_at`, `external_ref` | SaaS subscription billing distinct from restaurant sales. |
| `billing_payments` | `id`, `tenant_id`, `invoice_id`, `provider`, `provider_event_id`, `amount_minor`, `status` | Verified callbacks; unique provider event. |
| `service_orders` | `id`, `tenant_id`, `kind`, `status`, `amount_minor` | Optional assisted installation/hardware/setup package. |
| `onboarding_progress` | `tenant_id`, `branch_id NULL`, `step_code`, `completed_at`, `actor_id` | Resumable first-run guide. |

Plan feature values and limits are data-driven; tenant entitlements may vary by effective period. Historical access after expiry remains possible according to policy. Exact offline grace length, billing-provider integration and retention are launch decisions, not assumptions.

## 11. Gateway SQLite — local authoritative branch store

Gateway is provisioned for exactly one registered branch and contains no other tenant's operational data. Its schema is migration-versioned and mapped to cloud IDs.

| Local table | Purpose |
|---|---|
| `gateway_identity` | Tenant/branch/device identity, schema version, cloud endpoint, signed configuration reference. |
| `catalog_snapshot`, `price_snapshot`, `staff_permission_snapshot`, `entitlement_cache` | Versioned read models, encrypted/protected where necessary, with validity/expiry. |
| `local_table_visits`, `local_order_submissions`, `local_order_lines`, `local_payments`, `local_cash_sessions`, `local_stock_balances` | Transactional branch state projections. |
| `branch_operations` | Durable ordered command/event journal with unique operation ID, actor, entity, expected version, payload schema version, timestamp and monotonically increasing `branch_position`. |
| `sync_outbox` | Committed events pending cloud acknowledgement; retry metadata. |
| `sync_inbox` | Cloud commands/config updates deduplicated by message ID. |
| `sync_checkpoints` | Highest contiguous cloud-acknowledged branch position and cloud cursor. |
| `print_jobs`, `print_attempts` | Durable station output and uncertain delivery tracking. |
| `idempotency_results` | Operation ID → previously committed result, allowing safe client retries. |
| `gateway_health_events` | Recovery, errors and operator intervention log. |

**Local atomic commit:** in a single SQLite transaction validate command, update projections, append operation, append outbox entry and save idempotency result; acknowledge the client **only after commit**. Use WAL with a **single serialized writer**, bounded busy timeouts, integrity checks, backup and controlled schema migration. Gateway data at rest must be protected with OS-level ACLs and appropriate encryption/key handling (SQLite itself is not automatically encrypted).

## 12. Terminal SQLite — constrained cache

Tables: `terminal_identity`, `terminal_settings`, `catalog_cache`, `recent_read_models`, `entitlement_cache`, `pending_ui_actions`, `local_diagnostics`. No independent shared table/order write stream in normal operation. If gateway is unreachable, block conflicting shared writes and show a clear degraded state. Any later emergency cash-sale queue needs a separate ADR, reconciliation design and acceptance tests. Never cache raw payment-card data.

## 13. Sync protocol and conflict rules

**Command envelope (logical contract):**

```json
{
  "operation_id": "uuid",
  "tenant_id": "uuid",
  "branch_id": "uuid",
  "device_id": "uuid",
  "actor_id": "uuid",
  "entity_type": "table_visit",
  "entity_id": "uuid",
  "expected_version": 7,
  "schema_version": 1,
  "command_type": "append_order_submission",
  "occurred_at_utc": "2026-11-17T12:00:00Z",
  "payload": { "example_only": true }
}
```

This is a **contract illustration**, not a trusted client claim: tenant/branch/actor/device permissions are resolved from authenticated credentials and validated against registered device context.

**Sync transaction path:** (1) gateway commits locally; (2) gateway sends contiguous outbox batch with stable IDs and branch positions; (3) cloud authenticates gateway, checks branch ownership, validates schema/version and deduplicates `(tenant_id,branch_id,operation_id)`; (4) cloud persists event, updates projections and checkpoint in one PostgreSQL transaction; (5) gateway records per-operation result and advances contiguous acknowledged checkpoint; (6) retries never reapply accepted effects.

**Concurrency:** one gateway writer owns live branch operations; stale `expected_version` returns a conflict with current safe read model. Cloud-origin catalog changes are versioned and applied at a controlled boundary; cloud must not concurrently overwrite open visit/payment state. Out-of-order events wait or fail for reconciliation; do not skip sequence gaps.

**Partition cases:** WAN down / LAN up continues via gateway; gateway down / WAN up fails closed for shared writes unless an explicitly tested authority-transfer protocol exists; gateway + WAN down blocks shared writes. Customer QR remains cloud-dependent and cloud must route accepted QR orders to the correct branch gateway; QR orders cannot be promised as dispatched while branch is unreachable. Define branch availability/acceptance policy for cloud QR orders before launch (default: reject or hold as *not accepted* with clear customer messaging).

## 14. Critical constraints and indexes

Minimum PostgreSQL constraints to implement/test:

- Unique `(tenant_id,id)` for tenant-scoped parents and `(tenant_id,branch_id,id)` for branch-scoped parents; composite FKs on every branch-owned relationship.
- `UNIQUE(tenant_id,branch_id,operation_id)` on accepted branch events; unique `(tenant_id,branch_id,branch_position)` on ordered event log.
- Partial unique active table visit per `(tenant_id,branch_id,table_id)`.
- Partial unique open cash session per `(tenant_id,branch_id,register_id)`.
- Unique payment provider event/reference and idempotency key within the appropriate provider/merchant scope.
- Unique print dedupe key for `(ticket_id,printer_id,job_kind,revision)`.
- Check constraints for nonnegative finalized totals, bounded refund/allocation amounts via transactionally enforced rules, and positive quantities where required.
- Indexes: `(tenant_id,branch_id,created_at)` on orders, sales, payments, stock movements and audit; `(tenant_id,branch_id,status)` on active visits, tickets and jobs; `(tenant_id,branch_id,visit_id)` on submissions; `(tenant_id,branch_id,branch_position)` on events; subscription `(tenant_id,status,period_end)`.

**Row-Level Security:** evaluate and test RLS as defense in depth using transaction-scoped tenant context. Never use it to replace application authorization; connection pooling and privileged worker paths need explicit safeguards. Database credentials separated by service role; migrations use a separate privileged identity.

## 15. Transaction boundaries and data integrity examples

1. **Append order:** lock/compare active visit version → validate catalog/modifiers/prices → create submission/lines → generate station tickets/jobs → append branch operation/outbox → commit → acknowledge. All-or-nothing.
2. **Cash checkout:** lock sale/visit and open cash session → verify outstanding amount and cashier permission → record payment and allocation → update settlement projection → append event/outbox → commit; concurrent terminal attempt fails safely.
3. **Card checkout:** create intent → external certified flow → verify provider callback or server-side reconciliation → idempotently record confirmed payment/allocation → close only if fully settled. Never keep DB transaction open while waiting on provider.
4. **Cancel dispatched item:** append adjustment and approval record → issue station correction ticket → update outstanding balance consistently → commit.
5. **Stock sale:** append movement once per unique source sale line and update balance projection in the same transaction; reversals create compensating movements.
6. **Cloud reporting:** accepted event and derived financial projection commit atomically where feasible; async aggregates must expose freshness checkpoint and be rebuildable.

## 16. Security, privacy, backups and retention

- TLS for cloud and authenticated encrypted LAN channels; rotate device credentials and revoke lost terminals.
- Use envelope encryption/OS secret store for printer credentials, tokens and protected local data; never store payment secrets in plaintext.
- Back up PostgreSQL with point-in-time recovery strategy and regular restore tests. Back up gateway SQLite with transactionally consistent snapshots and tested recovery/reassignment procedure; copying a live WAL database naively is unsafe.
- Retain financial/audit records according to applicable Tunisian law and later country-specific policy, verified with qualified advisors; define privacy deletion/anonymization separately from mandatory fiscal retention.
- Audit privileged changes, role grants, price overrides, refunds, reprints, gateway reassignment, and subscription activation. Protect audit logs against unauthorized edits.
- Define concrete RPO/RTO, backup frequency, key rotation and disaster recovery before production; do not assert untested guarantees.

## 17. Migration, testing and acceptance gates

**Migration workflow:** versioned EF Core PostgreSQL migrations and independently versioned gateway/terminal SQLite migrations; schema contracts backward compatible across rolling desktop/gateway updates; dry-run migrations and rollback/recovery plan. EF Core is the proposed .NET data access default, subject to prototype confirmation.

**Mandatory automated/integration tests:**

1. Tenant A cannot read/write Tenant B data, including indirect FK references and reporting.
2. Branch A cannot attach orders, stations, cash sessions or payments to Branch B.
3. Two terminals cannot open two active visits for one table or settle the same balance twice.
4. Same command retried 100 times produces one business effect and one set of preparation tickets.
5. Gateway crash after local commit but before cloud acknowledgment safely replays once.
6. Cloud accepts ordered batches, detects gaps, and does not acknowledge uncommitted events.
7. Print failure and ambiguous outcome are visible; controlled reprint is audited.
8. Guest QR session cannot read another guest's submitted items on the same table.
9. Product/price/tax edits do not change finalized historical sale lines.
10. Cashier-only cash confirmation; waiter card initiation never implies card success.
11. Refund cannot exceed remaining refundable payment; partial/split allocations reconcile exactly.
12. Stock movement replay does not double-decrement stock; stock projection rebuild matches ledger.
13. Expired/revoked terminal entitlements behave according to approved grace policy.
14. Gateway loss blocks unsafe multi-terminal writes; recovery does not fork branch state.
15. Branch-local business-date reports reconcile to immutable sales/payments and refunds.

**Definition of done for this design:** schema and constraints reviewed; high-risk gateway/printing/payment flows prototyped; regulatory gaps recorded; first migrations generated only after architecture sign-off. This document intentionally specifies **logical schema**, not production-ready SQL or complete EF Core entity definitions.

## 18. Open decisions and ADR triggers

| Topic | Current position / next action |
|---|---|
| Tunisian VAT/fiscal invoices, numbering and archiving | Verify with local accounting/legal experts before financial migrations and pilot. |
| Mobile card acceptance | Validate certified Tunisian provider, terminal and callback flow; then define provider-specific schema. |
| Gateway SQLite encryption | Select tested OS key storage and database encryption strategy on target hardware. |
| QR order when branch unreachable | Decide explicit reject vs queued-unaccepted UX; never imply kitchen dispatch. |
| Offline licensing grace | Specify duration, signed claims, clock tamper handling and expiry UX. |
| Gateway failure | No automatic active-active failover; controlled recovery and optional emergency mode require ADR. |
| Multi-branch reporting consistency | Define sync freshness display, reporting cutoff and reconciliation behavior. |
| RPO/RTO and retention | Set measurable business targets and compliance requirements before launch. |
| Stock units, discounts, rounding | Approve precise edge-case rules and fixtures before migrations. |
| Cloud/gateway event contract | Prototype event versions, ordering, retries and backwards compatibility. |

## 19. Next deliverables

1. `04-UI-UX-SPECIFICATION.md`: screen inventory, design tokens, touch targets, workflows, loading/offline/error states, QR privacy and responsive waiter/KDS UX.
2. `05-DEVELOPMENT-ROADMAP.md`: incremental delivery, risk-first technical spike, Claude Design and Claude Code prompt workflow, test gates.
3. Implementation artifacts after technical validation: ERD, EF Core entity mappings, PostgreSQL migrations, SQLite migrations, API/event JSON schemas, ADRs and automated test fixtures.

**Change control:** Any material departure from this design requires a concise ADR in `DECISIONS.md` describing context, alternatives, chosen solution, consequences and migration impact.
