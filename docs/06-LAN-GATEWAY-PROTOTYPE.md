# 06 — LAN gateway: technical validation (Phase 1.3)

Status: **prototype**, validated on one Windows 11 PC (gateway + two POS processes over loopback).
Not validated: multiple physical machines, Wi-Fi, Windows Service installation (see §8).

## 1. Goal and scope

Prove that two Windows POS terminals can coordinate restaurant orders through a local gateway with no
internet. In scope: terminal pairing and authentication, shared draft orders (create, add items, change
quantities, submit), real-time updates, idempotency, optimistic concurrency, durable print intents,
gateway/terminal failure recovery. Out of scope: cloud sync, billing, QR ordering, waiter app, tables,
payments, catalog sync, automatic failover.

## 2. Architecture

```text
 POS A (WPF + WebView2 + React)             POS B
   React ──bridge──► GatewayClient            (same)
                      │  HTTPS (TLS pinned) — commands, snapshot, operation resolution
                      │  WebSocket/SignalR (same TLS) — "orderChanged" notifications only
                      ▼
            Pos.Gateway (ASP.NET Core minimal API + SignalR, Kestrel HTTPS only)
              ├── TerminalAuth   pairing codes, challenge/response, bearer tokens (memory)
              ├── OrderService   single writer: idempotency → validate → apply → journal → commit
              ├── PrintWorker    Phase 1.2 print queue, unchanged
              └── gateway.db     SQLite (WAL, synchronous=FULL)
```

| Piece | Location |
|---|---|
| Wire contract (shared by both sides) | `apps/windows-pos/Pos.Core/Gateway/GatewayContracts.cs` |
| Gateway host, CLI, Windows Service | `apps/branch-gateway/Pos.Gateway/Program.cs` |
| Authoritative store + schema | `apps/branch-gateway/Pos.Gateway/GatewayStore.cs`, `OrderService.cs` |
| Terminal client (TLS pin, login, reconnect, recovery) | `apps/windows-pos/Pos.Core/Gateway/GatewayClient.cs` |
| Terminal identity + pending operations (POS SQLite v3) | `apps/windows-pos/Pos.Core/Gateway/TerminalStore.cs` |
| UI | `apps/pos-web/src/gateway/` |

**Authority.** The gateway's SQLite file is the only writer of shared order state. Terminals keep a
read-only cache (rebuilt from a snapshot after every connection). There is no terminal-side queue of
shared writes: while the gateway is unreachable, shared-order changes are refused ("fail closed"). The
existing local single-till sale ("Vente directe") keeps working, as before, without the gateway.

**Gateway database.** The gateway reuses the POS schema (catalog with routing, print destinations,
`print_jobs`) so the Phase 1.2 printing subsystem runs unchanged, and adds: `gateway_identity`, `terminals`,
`enrollment_codes`, `shared_orders`, `shared_order_lines`, `operations` (journal + idempotency store,
`position` = branch log cursor) and `print_intents`.

## 3. Commands, idempotency and concurrency

Every mutation carries a client-generated `operationId` (GUID, one per operator action):

| Operation | Version check | Why |
|---|---|---|
| `create_order` | — | new entity |
| `add_item` (+n of a product) | **no** | additive and commutative: two tills adding items both win |
| `set_quantity` (incl. 0 = remove) | `expectedVersion` | overwrite: a stale edit would silently erase another till's change |
| `submit` | `expectedVersion` | the operator must submit exactly what they saw |

One SQLite transaction (`BEGIN IMMEDIATE`, plus an in-process lock) per command:

1. Look up `operationId` in `operations`. If it is found with the same terminal and the same request
   hash, the stored response is returned with `replayed: true` and nothing is re-applied. A different
   terminal or payload gets `409 idempotency_key_reused`. A burned ID gets `409 operation_abandoned`.
2. Validate (order open, version, product active, quantities 1–99 per add and ≤ 999 per line, label ≤ 60).
   Prices and names are read from the gateway catalog; terminals never send prices.
3. Apply the change, bump `version`, append to `operations`, store the response for replay.
4. On `submit`, in the same transaction, for each station that has items: one `print_jobs` row
   (the Phase 1.2 queue) and one `print_intents` row. `PRIMARY KEY (order_id, station_id)` makes a
   second intent impossible even if the idempotency layer were bypassed.
5. Commit, then reply. Then broadcast `orderChanged(position, order)` to the branch's SignalR group.

A conflict returns `409 version_conflict` with the current order. The terminal updates its cache and the
UI tells the operator who changed the order.

## 4. Security model (LAN is not trusted)

| Threat | Control |
|---|---|
| Eavesdropping / tampering on the LAN | HTTPS only (no plain-HTTP listener); TLS 1.2/1.3 by Kestrel defaults |
| Rogue gateway / MITM | Terminals **pin** the gateway certificate SHA-256 fingerprint received out of band at pairing (self-signed ECDSA P-256; no CA trust) |
| Rogue device | Pairing requires a **one-time code** (100 bits, 15 min, SHA-256 stored, single use) created on the gateway by an administrator + proof of possession of the terminal's private key |
| Stolen credentials | No shared secret anywhere. Each terminal has its own ECDSA P-256 key pair (private key DPAPI-protected, CurrentUser). Login = signature of a single-use 60 s nonce bound to branch + terminal + gateway fingerprint. Tokens are 256-bit random, 1 h, stored hashed, memory only |
| Lost/compromised terminal | `Pos.Gateway revoke --terminal <id>`: HTTP rejected immediately, live SignalR connection closed within 5 s, re-login refused |
| Cross-branch access | One gateway = one branch. Token bound to the branch; `/v1/branches/{id}` must match it (`403 branch_mismatch`) |
| Unauthenticated endpoints | Deny-by-default fallback authorization policy. Only `enroll`, `auth/challenge` and `auth/token` are anonymous, and they are rate limited (10 and 60 requests per minute per IP). The hub requires a token in the header, never in the query string |
| Exposure beyond the LAN | Connections from non-private source addresses are dropped (loopback, RFC 1918, link-local and ULA only). The install script opens the firewall for the Private profile and the local subnet only |
| Malformed input | Strict JSON (unknown fields, missing required fields and wrong types are rejected), 64 KB body limit, GUID validation, bounded strings and quantities; errors never contain internals |
| Configuration injection | The gateway ignores environment and appsettings configuration: no variable can add a listener |
| Data at rest | The TLS key is a DPAPI-encrypted PKCS#12. The data folder ACL is SYSTEM, Administrators and the service account (install script). **The SQLite file itself is not encrypted** (see risks) |

Assumptions: the gateway PC and its administrator account are trusted. Pairing codes and fingerprints
are transmitted out of band (screen of the gateway PC → typed on the POS). A Windows user who can run
code on a paired POS can act as that terminal: there is no staff authentication yet.

## 5. Failure handling

| Failure | Behaviour (tested) |
|---|---|
| Gateway crash / restart | Terminals detect it (WebSocket close, or a 15 s server timeout), show **Passerelle hors ligne** and refuse shared writes. They retry every 0.5–5 s indefinitely, log in again (tokens are lost with the process), subscribe, then reload the full snapshot. State is identical before and after |
| POS disconnect / restart | Stable identity (same GUID and key) and automatic reconnection without re-pairing. The snapshot is reloaded |
| Duplicate request (double tap, retry after a timeout) | The same `operationId` returns the original result; nothing is applied twice |
| Concurrent updates | Additions all apply. Overwrites and submissions on a stale version are rejected with `version_conflict` and the current state |
| Request committed but the response was lost | Before sending, the terminal stores the operation in `gateway_pending_operations` (POS SQLite). Retries reuse the ID. When the outcome is unknown, on the next connection it calls `operations/{id}/resolve`: the gateway either returns the result (**applied once**) or **burns** the ID (`abandoned`), so a delayed copy can never apply later. The operator gets a notice in both cases. A test kills the gateway between commit and reply |
| Gateway crash mid-transaction | SQLite atomicity: the transaction is rolled back and nothing is acknowledged |
| SQLite recovery | WAL with `synchronous=FULL`. `quick_check` runs at every start, and a corrupt file **stops the gateway** with an error rather than serving bad state. `Pos.Gateway backup --out f.db` makes an online, transactionally consistent copy using the SQLite backup API, verified with `integrity_check` |
| Print side | An intent is a durable Phase 1.2 job (`queued` → `sent`/`failed`/`uncertain`). Printer outages, retries and uncertain outcomes behave as documented for Phase 1.2 |

## 6. The gateway is a single point of failure

While the gateway is down, **no terminal can create, change or submit shared orders**. This is deliberate:
a terminal that kept writing alone would fork the branch state. Terminals **never** promote themselves to
gateway. Local single-till sales ("Vente directe") and local printing still work.

### Proposed controlled recovery procedure (manual, operator-initiated)

> Superseded in Phase 1.5 by [08-BACKUP-RESTORE-RECOVERY.md](08-BACKUP-RESTORE-RECOVERY.md): verified scheduled backups, `restore`, cloud `reconcile`, terminal unpair/replacement.

1. **Restart in place** (most incidents). The Windows Service restarts automatically on crash (5 s, 10 s,
   then 60 s). Terminals reconnect by themselves. Check `Pos.Gateway status` and the logs in `<data>\logs`.
2. **Corrupt database.** The gateway refuses to start. Stop the service and keep the corrupt file for
   analysis. Restore the latest backup (`gateway.db` from `Pos.Gateway backup`) and restart. Orders made
   after that backup are lost from the gateway, so re-enter them from the paper tickets already printed.
   Tell staff before reopening.
3. **Gateway PC dead: controlled reassignment to a standby PC.**
   a. Make sure the old gateway is **off and stays off**, by unplugging it from the network. Two live
      gateways must never coexist.
   b. Install the gateway on the standby PC (`install-gateway-service.ps1`) with the **same branch ID**,
      and restore the latest `gateway.db` backup into its data folder.
   c. The standby has a new TLS certificate, so terminals reject it: their fingerprint pin does not match.
      This is intended. Re-pair each terminal (new code + new fingerprint). Re-pairing needs a manual
      "unpair" on the POS, which is **not implemented in the prototype** (see risks).
   d. Revoke the old terminal entries if any device is unaccounted for.
4. **Before the next phase:** scheduled automatic backups to a second disk or PC, a health endpoint for
   monitoring, a documented UPS recommendation, a signed "gateway reassignment" record so terminals can
   accept a new certificate without full re-pairing, and a decision (ADR) on any restricted emergency mode.

## 7. Tests

| Suite | What it proves |
|---|---|
| `Pos.Gateway.Tests` (28 tests in Debug, 27 in Release where the fault-injection test is compiled out; real `Pos.Gateway.exe` process + two simulated terminals using the production `GatewayClient`) | Security: every endpoint and the hub reject unauthenticated access, no plain HTTP, single-use pairing with key possession, certificate pin mismatch, branch binding, nonce reuse, revocation, malformed and oversized bodies. Orders: cross-terminal propagation, 40 concurrent additions kept, conflicting overwrite rejected, stale submission refused, duplicate submission gives one order and one intent per station, idempotency key reuse refused, server-side prices and validation. Recovery: crash/restart with fail-closed and identical state, terminal restart, crash between commit and reply resolved exactly once (Debug-only fault injection), abandoned operation, online backup, corrupt database refused |
| `tests/e2e/lan-gateway.mjs` | Two real POS desktop apps (UI driven over CDP) + a real gateway: A creates, B sees, concurrent edits, conflict, A submits, duplicates rejected (DB checked), gateway hard-killed and restarted, both recover the exact state, B restarts with the same identity |
| `Pos.Core.Tests` (existing) | Bridge allowlist now includes the 6 gateway commands; schema v3 migration |

## 8. Not validated here (needs multiple physical Windows machines)

1. Two or more POS PCs and a separate gateway PC on a real switch and on Wi-Fi: latency, reconnect times,
   behaviour when a cable is unplugged (half-open TCP, detected by the 15 s server timeout).
2. Windows Firewall rule (Private profile, local subnet) and the gateway reached by IP or DNS name from
   another PC. Laptops that move between networks (Public profile).
3. Installation as a Windows Service under `NT SERVICE\PosSaaSGateway`: DPAPI certificate creation by
   the service account, data folder ACL, automatic restart policy, start at boot before the POS.
4. Power cut on the gateway PC (not just a process kill): SQLite durability with `synchronous=FULL` on
   the target disk and controller cache.
5. Network partition: one POS isolated while the other keeps working, then rejoining.
6. Clock skew between PCs (tokens and codes use gateway time only, but logs are compared across machines).
7. Printing from the gateway PC to real station printers (Phase 1.2 hardware tests still apply).
8. Controlled reassignment to a standby PC (§6, step 3), end to end.

## 9. Remaining risks

- **Single point of failure** (§6). Recovery is manual and not yet automated or rehearsed.
- No staff authentication or authorization on terminals. Anyone using a paired POS acts as it.
- The gateway SQLite file is not encrypted at rest (only ACLs). A SQLCipher-style option needs evaluation.
- There is no unpair/re-pair flow on the POS yet. A changed gateway certificate requires deleting the
  POS `gateway_link` row manually.
- The terminal catalog is not synchronized from the gateway. Both use the same seed today, and the gateway
  catalog is authoritative for prices and names.
- Tokens and pending challenges live in memory: a gateway restart forces every terminal to log in again
  (intended, but it causes a burst of logins).
- Version conflicts surface to the operator; there is no automatic merge for overwrites.
- The in-process lock plus `BEGIN IMMEDIATE` serializes all writes. That is fine for a branch, but
  throughput was not measured on target hardware.
- Fault injection exists only in Debug builds (compiled out of Release).
