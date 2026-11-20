# 07 — Cloud synchronization and recovery: technical validation (Phase 1.4)

Status: **prototype**, validated on one Windows 11 PC: real PostgreSQL 18 (throwaway clusters started by the
tests), real cloud API process, real gateway processes with SQLite, one real POS desktop app in the E2E.
Not validated: a hosted cloud, real WAN conditions (latency, proxies, NAT), PostgreSQL failover, several
physical branches.

## 1. Goal and scope

Prove that orders taken at a branch during an internet or cloud outage reach the cloud **exactly once at
the logical-record level**, using **at-least-once delivery plus idempotent processing**. The network is not
claimed to deliver exactly once: batches are resent until acknowledged and the cloud recognises replays.

In scope: Organization → Business → Branch tenancy, per-gateway credentials, durable gateway outbox,
ordered resumable sync, cloud idempotency, minimal scoped reporting. Out of scope (by request): billing,
dashboard, customer QR, catalog management in the cloud, payments, **cloud → gateway catalog sync**,
automatic gateway failover.

## 2. Architecture

```text
 POS tills ──LAN (Phase 1.3)──► Pos.Gateway (authoritative for the branch)
                                  ├── OrderService: operation + outbox row in ONE SQLite transaction
                                  ├── gateway.db: sync_outbox, sync_state, cloud_link (schema v2)
                                  └── CloudSyncWorker ──HTTPS, signed requests──► Pos.Cloud (ASP.NET Core)
                                                                                    ├── GatewaySignatureHandler
                                                                                    ├── SyncService (one PG transaction per batch)
                                                                                    ├── Report endpoints (scoped keys)
                                                                                    └── PostgreSQL (one shared multi-tenant DB)
```

| Piece | Location |
|---|---|
| Wire contract (shared) | `apps/shared/Pos.Sync.Contracts/SyncContracts.cs` |
| Cloud host, CLI | `apps/cloud-api/Pos.Cloud/Program.cs` |
| Schema (EF Core) + migration | `apps/cloud-api/Pos.Cloud/CloudDb.cs`, `Migrations/` |
| Batch processing / idempotency | `apps/cloud-api/Pos.Cloud/SyncService.cs` |
| Authentication | `apps/cloud-api/Pos.Cloud/CloudAuth.cs` |
| Endpoints, error mapping | `apps/cloud-api/Pos.Cloud/CloudApi.cs` |
| Gateway outbox / sync state | `apps/branch-gateway/Pos.Gateway/GatewayStore.Sync.cs` (+ migration 2 in `GatewayStore.cs`) |
| Gateway sync worker | `apps/branch-gateway/Pos.Gateway/CloudSyncWorker.cs` |
| Tests | `apps/cloud-api/Pos.Cloud.Tests/`, `tests/e2e/cloud-sync.mjs` |

**Authority.** The branch gateway remains the only writer of operational orders. The cloud stores a
*projection* of what gateways report; it never sends order changes back and never overwrites a gateway
transaction. Conflicting content is refused, not merged (§4.4).

## 3. Schema

### 3.1 Cloud (PostgreSQL, migration `InitialCloudSchema`)

| Table | Key | Notes |
|---|---|---|
| `organizations` | `id` | |
| `businesses` | `id`, alt key `(organization_id, id)` | FK → organization |
| `branches` | `id`, alt key `(organization_id, business_id, id)` | FK `(organization_id, business_id)` → businesses |
| `gateway_credentials` | `id` | branch chain, ECDSA P-256 public key (SPKI), unique key hash, `revoked_at`, `last_seen_at` |
| `provisioning_codes` | `code_hash` | SHA-256 only, branch chain, `expires_at`, `used_at`, `used_by_gateway_id` |
| `branch_sync_states` | `branch_id` | `last_sequence` (checkpoint, ≥ 0), `last_sync_at` |
| `sync_events` | `(branch_id, sequence)`, unique `(branch_id, event_id)` | immutable received event: `jsonb` payload, SHA-256 hash, gateway id |
| `orders` | `(branch_id, id)`, unique `(branch_id, number)` | projection; checks on status, version ≥ 1, totals ≥ 0; `last_sequence` |
| `order_lines` | `(branch_id, order_id, product_id)` | quantity 1–999 |
| `branch_products` | `(branch_id, product_id)` | catalog as **published by that branch** |
| `report_keys` | `id` | SHA-256 of the secret, scope (organization / business / branch) with a CHECK on its shape |

Every branch-owned table carries `(organization_id, business_id, branch_id)` with a **composite foreign key**
to `branches(organization_id, business_id, id)`, so the database itself refuses a row whose tenant chain is
inconsistent (tested). Snake-case naming throughout.

Database roles: migrations run with the owner role (`POS_CLOUD_MIGRATION_DB`); the running API and admin
commands use a data-only role (`POS_CLOUD_DB`, DML only, no DDL — tested). `run` refuses to start if
migrations are pending.

### 3.2 Gateway (SQLite, schema v2)

| Table | Notes |
|---|---|
| `sync_outbox` | `sequence` (contiguous, PK), `event_id` UNIQUE, type, operation position, immutable JSON payload, attempts, `acked_at` |
| `sync_state` | single row: `acked_sequence`, `catalog_hash`, last success/attempt, `consecutive_failures`, `last_error`, `blocked_sequence` |
| `cloud_link` | single row: cloud URL, certificate pin, pending code, `gateway_id`, tenant ids/names **as returned by the cloud**, DPAPI-protected private key |

**Existing identities are preserved.** The gateway's branch id (Phase 1.3) is reused as the cloud branch id
(`branch-create --id <gateway branch id>`); terminals, orders and operations are untouched. Migration 2
backfills one `order_imported` snapshot per existing order, so a Phase 1.3 gateway uploads its history once
linked (tested by downgrading a gateway database to schema 1).

## 4. Synchronization protocol

### 4.1 Outbox (gateway)

Each committed operation (`create_order`, `add_item`, `set_quantity`, `submit`) writes its outbox row in the
**same SQLite transaction** as the order change. The row is the full order snapshot after the operation
(plus print stations for `submit`). The sequence is `MAX+1` under SQLite's write lock, so it is contiguous.
Event id = operation id. Idempotent replays of a terminal operation do not create a new outbox row.
Catalog changes produce a `catalog_published` event when the local catalog hash changes (one-way).

### 4.2 Delivery (gateway → cloud)

`POST /v1/sync/events` with `{ "events": [...] }`: the events after `acked_sequence`, in order, at most
100 events and 1.5 MB (`--sync-batch` lowers the count). The request carries **no tenant or branch id**;
unknown JSON fields are rejected.

Response `SyncBatchResult { lastAcceptedSequence, outcomes[], rejected? }`. The gateway marks events
acknowledged **only up to `lastAcceptedSequence`**, then continues immediately while a backlog remains,
otherwise waits for a signal (new outbox row) or the idle poll (5 s).

Failures (network, timeout, 5xx, 401…) are recorded in `sync_state` and retried with exponential backoff
1 s, 2 s, 4 s … up to `--sync-max-backoff` (default 60 s), ±20 % jitter. Sync errors never stop the gateway
or affect order taking.

### 4.3 Processing (cloud), per batch

One PostgreSQL transaction; the branch's `branch_sync_states` row is locked (`SELECT … FOR UPDATE`), which
serialises concurrent batches of a branch. For each event, in order:

| Situation | Result |
|---|---|
| `sequence ≤ checkpoint`, same event id and same payload hash | `duplicate` — acknowledged, **not re-applied** |
| `sequence ≤ checkpoint`, different id or content | `sequence_conflict` (stop) |
| `sequence = checkpoint + 1`, event id already used elsewhere | `sequence_conflict` (stop) |
| `sequence > checkpoint + 1` | `sequence_gap` with `expectedSequence` (stop) |
| fails validation (schema version, type, totals, line limits, order regression, order-number reuse…) | `event_rejected` (stop) |
| otherwise | stored in `sync_events`, applied to the projection, checkpoint advanced |

Accepted events before a stop are committed; nothing after it is. Uniqueness on `(branch_id, sequence)`,
`(branch_id, event_id)`, `(branch_id, order id)` and `(branch_id, order number)` backs this in the database.
A projection update never lowers an order's version.

### 4.4 Unresolved operations are never discarded

| Cloud answer | Gateway behaviour |
|---|---|
| `event_rejected` / `sequence_conflict` | sync **blocked** at that sequence (`blocked_sequence`, `last_error`, shown by `status`), retried with backoff; the event is never skipped or deleted. Requires an operator decision (tested: blocked, then resumes after the event is repaired). |
| `sequence_gap` with `expected − 1 < acked` (the cloud lost data it had acknowledged, e.g. restored from backup) | the gateway **rewinds** `acked_sequence` and resends (not covered by an automated test). |
| `sequence_gap` with `expected − 1 > local max` (cloud knows more than the gateway: gateway restored from an older backup) | blocked, manual reconciliation. |

Outbox rows are never deleted by this prototype (no compaction yet).

## 5. Failure and recovery behaviour (tested)

| Scenario | Observed result |
|---|---|
| Cloud down, orders taken, gateway hard-killed and restarted during the outage | outbox rows survive (SQLite, WAL); failures recorded; after recovery every order appears once, cloud events = outbox |
| Acknowledgment lost after commit (`POS_CLOUD_FAULT=drop-ack:N`, Debug builds) | gateway resends; cloud answers `duplicate`; counts unchanged |
| Cloud crashes after commit, before responding (`crash-after-commit:1`) | same as above after restart |
| Cloud crashes before commit (`crash-before-commit:2`) | transaction rolled back; gateway resends; nothing lost or doubled |
| Cloud killed while a 25-order backlog drains in batches of 1 | resumes from the checkpoint after restart; exact match |
| Concurrent identical batches (8 in parallel) | each event accepted exactly once |
| Overlapping batches, replays, gaps, conflicting content | duplicate / gap / conflict as in §4.3 |
| Invalid event inside a batch | batch committed up to it, sync blocked there |

Fault injection is compiled only into Debug builds; the Release build has none (those tests are excluded).

## 6. Security and tenant isolation

- **Gateway credential**: one ECDSA P-256 key pair per gateway, generated on the gateway. The private key
  is DPAPI-protected (CurrentUser, specific entropy) in `gateway.db`; the cloud stores only the public key.
- **Provisioning**: `gateway-code --branch` issues a one-time code (≈100 bits, stored hashed, 15 min
  expiry). `Pos.Gateway cloud-link --url --code [--pin]` stores it; the running gateway registers with
  **proof of possession** (signature over code, its branch id and public key). The cloud checks the code is
  unused, unexpired, row-locked, and that it was issued **for this gateway's own branch id** (`409
  branch_mismatch` otherwise). Tenant ids are then **returned by the cloud**, not supplied by the gateway.
  The registration endpoint is rate-limited (10/min per IP).
- **Requests**: `Authorization: PosGateway {gatewayId}:{unixSeconds}:{signature}` over method, path,
  timestamp and body SHA-256, ±5 min window. The branch for a sync batch comes **only** from the
  authenticated credential; the body cannot name one. Revocation (`gateway-revoke`) takes effect at the next
  request; the gateway keeps its outbox and reports the refusal.
- **Reports**: `report-key --org [--business [--branch]]` issues `posrk.<id>.<secret>`; only SHA-256 is
  stored, compared in constant time. A key sees only its scope; anything else, existing or not, is `404`
  (no probing). Endpoints: organization, business and branch summaries; branch orders; branch catalog.
- **Transport**: HTTPS only (no HTTP listener). The development certificate (`dev-cert`) is pinned by
  SHA-256 on the gateway; with no pin, normal CA validation applies.
- **Hardening**: configuration sources limited to code, deny-by-default authorization, 2 MB body limit,
  strict JSON, errors without internals, data-only database role.
- **Isolation tests**: 2 branches in one business, 2 businesses in one organization, plus a second
  organization; per-branch order numbering and catalogs; report keys at each scope; a credential of
  another organization reusing a victim's order id and number lands only in its own branch; inconsistent
  tenant chains rejected by PostgreSQL foreign keys.
- Existing gateway security (Phase 1.3 terminal pairing, TLS pinning, tokens) is unchanged.

Known gaps: no PostgreSQL row-level security (isolation is enforced by queries + composite keys); a captured
request could be replayed within its 5-minute window, which is harmless for sync (idempotent) but would
re-run a report read; report keys have no expiry or rotation UI; no audit log of report access.

## 7. How to try it

```powershell
.\scripts\build.ps1
$env:POS_CLOUD_MIGRATION_DB = "Host=...;Username=owner;Password=...;Database=pos_cloud"
$env:POS_CLOUD_DB           = "Host=...;Username=pos_app;Password=...;Database=pos_cloud"   # DML only
$env:POS_CLOUD_CERT_PASSWORD = "<12+ chars>"
$env:POS_CLOUD_PUBLIC_ORIGIN = "https://dashboard.example.tn"   # links in account emails (doc 12)
$env:POS_CLOUD_MAIL_DIR      = "C:posmail"                    # existing folder: account emails land here as .eml (or POS_CLOUD_SMTP_HOST, doc 13)
$env:POS_CLOUD_DATA_KEYS     = "<output of Pos.Cloud data-key-new, after data_key=>"   # encrypts MFA secrets at rest
$cloud = "apps\cloud-api\Pos.Cloud\bin\Debug\net10.0\Pos.Cloud.exe"
& $cloud migrate                     # then GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES ... TO pos_app
                                     # and REVOKE UPDATE, DELETE ON auth_audit_events FROM pos_app
& $cloud dev-cert --out cloud.pfx --host 127.0.0.1          # prints sha256=<pin>
& $cloud org-create --name "Groupe"                          # organization_id=...
& $cloud business-create --org <org> --name "Café"           # business_id=...
& $cloud branch-create --business <biz> --name "Centre" --id <gateway branch id from 'Pos.Gateway status'>
& $cloud gateway-code --branch <branch>                      # code=...
& $cloud run --listen https://127.0.0.1:8443 --certificate cloud.pfx
Pos.Gateway.exe cloud-link --url https://127.0.0.1:8443 --code <code> --pin <sha256>   # gateway running
& $cloud report-key --org <org> --name "Compta"              # report_key=posrk...
```

`tests/e2e/cloud-sync.mjs` performs exactly this against a throwaway PostgreSQL cluster.

## 8. Remaining limitations

- No cloud → gateway catalog sync; the cloud only stores what each branch publishes.
- One gateway per branch, no failover; a branch restored from an old backup needs manual reconciliation
  (detected as conflict/gap, never silently merged). No reconciliation tooling yet.
- Outbox never compacted; snapshot-per-operation payloads grow with order size.
- No PostgreSQL RLS; no hosted deployment, secrets management, monitoring or alerting.
- Development certificate + pinning only; production needs a CA certificate and rotation of pins/keys.
- Timestamps are stored at microsecond precision (PostgreSQL); the gateway keeps 100 ns.
- Reports are minimal summaries, not a dashboard; no pagination beyond `limit` (≤ 500).
- Validated on one PC over loopback; real WAN behaviour, clock skew beyond ±5 min and large backlogs
  (thousands of events) were not measured.
