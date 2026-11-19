# 09 — Reliability validation and production-readiness assessment (Phase 1.6A)

This phase checks the foundation built in Phases 1–1.5 under load and under faults. It adds **no product
features**. Every number below comes from **one Windows PC over loopback**. It is software-path evidence, **not
validation of physical hardware, a real LAN, Wi-Fi, printers or a hosted cloud.** See §7 for the hardware
checklist that remains to be done.

## 1. Environment

| Item | Value |
|---|---|
| Machine | 1 PC, Windows 11 Pro 10.0.26200, 8 logical processors, 14 GB RAM (≈ 3 GB free during runs) |
| Builds | **Release** (reported numbers); the same scenarios also passed on Debug |
| Gateway | Real `Pos.Gateway.exe` (ASP.NET Core, SQLite WAL `synchronous=FULL`), own data folder per scenario |
| Cloud | Real `Pos.Cloud.exe` (ASP.NET Core, EF Core/Npgsql), HTTPS with a dev certificate |
| Database | PostgreSQL 18, **a private cluster created by the tests** under `%TEMP%` (`initdb` + `pg_ctl`, own port). The installed PostgreSQL service is never touched. |
| Terminals | `SimTerminal` = the real `Pos.Core` `GatewayClient` + `TerminalStore` (SQLite), without the WPF/WebView2 shell |
| Network | Loopback. Faults go through an in-test TCP relay (`TcpProxy`): reset, silent loss, delayed responses. TLS passes through it unchanged, so certificate pinning still applies. |

## 2. Method

- Scenarios live in `apps/cloud-api/Pos.Cloud.Tests/ReliabilityTests.cs`. They are **opt-in** because they are long and use a lot of
  memory. A normal `dotnet test` lists them as skipped.

  ```powershell
  $env:POS_RELIABILITY = "1"; $env:POS_RELIABILITY_OUT = "$PWD\reliability.jsonl"
  dotnet test apps/cloud-api/Pos.Cloud.Tests -c Release --filter "FullyQualifiedName~ReliabilityTests.L1"   # one at a time
  ```
- Run **one scenario at a time**, and stop any leftover processes and test clusters between runs (memory limits on this PC).
- Every scenario **asserts correctness** before it records any measurement:
  - the gateway SQLite and the cloud PostgreSQL agree **row by row** for every order (number, status, version, quantities,
    totals);
  - every outbox event exists exactly once in the cloud, contiguous (`COUNT = COUNT DISTINCT = MAX(sequence)`);
  - no terminal is left with unresolved operations.
- Latency is measured by the client, around each real `GatewayClient` call (HTTPS, auth, SQLite commit,
  SignalR broadcast). Memory is the process working set, sampled every 200 ms.
- Rate limits shaped the scenario sizes. The gateway allows 10 enrolments/min and 60 anonymous auth requests/min
  **per IP**. All simulated terminals share 127.0.0.1, so scenarios use at most 8 terminals. On a real LAN each till has
  its own address.

## 3. Results (Release)

### Load

| Scenario | Result |
|---|---|
| **L1** 8 terminals × 25 orders (create, 2 adds, submit = 800 ops) concurrently | **2.37 s, 337 ops/s**. Latency p50/p95/p99: create 19/66/169 ms, add 18/32/168 ms, submit 22/44/48 ms. 201 orders (200 + the contended one), numbers 1–201 unique, all submitted. |
| L1 contention: 8 terminals × 10 adds on the **same** order | 80 adds in 0.17 s, p95 28 ms. Final quantity 80, version 82, nothing lost. |
| L1 submit race: 8 terminals submit the same version | **1 applied, 7 definitive refusals** (`order_already_submitted`). No pending operations. |
| L1 sync lag after the last operation | 4.85 s to cloud = gateway (883 events). |
| L1 memory | Gateway 130 MB working set (65 MB private). Cloud 170 MB (82 MB private). |
| **L2** backlog of **2,004 events** built with the cloud down | Built offline at 418 ops/s; SQLite 9 MB. After the cloud started: first ack 2.0 s, **drained in 14.8 s (136 events/s, batches of 100)**. Exactly once. Memory flat (gateway 139 MB, cloud 167 MB peak). |
| **L4** 20 identical `create` requests at once (same operation id) | **1 order**, 20 × HTTP 200 with the same order, in 0.10 s. Same id with a different body → 409. |
| L4 200 add-item requests cut at a random 0–25 ms | 82 completed, 118 had an unknown outcome. Resolution: 45 applied, 73 abandoned (resolve p50 1.7 ms). Final quantity = completed + applied (127), exactly once. Late copies of abandoned ids are refused; replays of applied ids change nothing. |
| **L5** online backups under write load (64 MB database, 4 terminals) | Baseline write p99 **21 ms**. **Scheduled in-process backups every 10 s: p99 29.5 ms**, each backup 0.19–0.35 s (copy + `integrity_check` + SHA-256 + retention). Back-to-back CLI backups (worst case): p99 356 ms, max 500 ms, throughput about −50 %. **All 24 backups validated**, and the last one restored into a fresh gateway with the same outbox sequence. |

### Recovery

| Scenario | Result |
|---|---|
| **R1** gateway hard-killed, 6 terminals connected (×2) | Outage detected in 0.05 s. Process start → listening **1.0 s** → all terminals reconnected **1.4–1.9 s**. First write 36 ms. No lost or duplicated operation. |
| **L3** gateway killed mid-drain (1,002-event backlog, batches of 10) | Listening 1.0 s, next ack **1.0 s** after restart. |
| L3 cloud killed mid-drain (down 2 s) | Cloud listening 2.0 s, next ack **3.2 s** after restart (gateway backoff). |
| L3 **PostgreSQL immediate stop + restart** mid-drain (private cluster) | Restart 1.9 s. The cloud process survived; requests during the outage got a generic 503 and were logged (6 error lines). **Next ack 0.02 s after PostgreSQL was back.** Total drain 19.6 s. 0 duplicate batches, stores identical. |
| **R2** terminal ↔ gateway reset (cable/switch) | Detected in 0.06 s. Writes refused with `gateway_unavailable` (fail closed, nothing queued). Reconnected ≤ 5.1 s after restore (reconnect backoff 0.5/1/2/5 s). |
| R2 silent loss for 8 s with an operation in flight | The client waited 8.1 s; the operation was applied **once** after the link returned. The silent outage was not reported as "offline" (SignalR keep-alive timeout ≈ 30 s). |
| **R3** cloud link refused for 45 s (**production sync settings**: timeout 30 s, max backoff 60 s, poll 5 s) | Resynced **22 s** after the link returned (backoff with jitter; bounded by about 60 s). |
| R3 silent cloud-link loss for 20 s (shorter than the 30 s timeout) | Resynced 0.4 s after the link returned. |
| R3 every cloud response delayed 3 s | 15 events synced in 12.2 s. Steady-state lag without delay: 0.36 s. |
| **R4** responses delayed 7 s against a 5 s timeout (lost/late acknowledgments) | Gateway timed out and resent (max 2 attempts per event). The cloud recognized 1 duplicate batch and stored nothing twice. Synced 0.23 s after the delay was removed. |

## 4. Pass/fail criteria

| Criterion | Threshold | Result |
|---|---|---|
| No lost operation, no duplicate, gateway = cloud | Exact, every scenario | **Pass** (all 9 scenarios) |
| Concurrent submit of one version | Exactly one wins, others refused | **Pass** |
| Identical / interrupted requests | Applied at most once; every unknown outcome resolved | **Pass** |
| Write latency, 8 terminals | p95 < 100 ms, p99 < 500 ms | **Pass** (p95 ≤ 66 ms, p99 ≤ 169 ms) |
| Gateway restart → terminals usable | < 10 s | **Pass** (≤ 1.9 s) |
| Backlog of 2,000 events drains | < 2 min, exactly once | **Pass** (14.8 s) |
| Cloud/PostgreSQL restart → sync resumes by itself | < 2 min, no operator action | **Pass** (≤ 3.2 s test settings; ≤ 60 s by design with production backoff) |
| Scheduled backup impact | p99 < 100 ms during backups | **Pass** (29.5 ms) |
| Backups taken under load | 100 % validate and restore | **Pass** (24/24) |
| Memory | No growth across a run; < 300 MB per process | **Pass** (gateway ≤ 139 MB, cloud ≤ 170 MB) |

These thresholds are prototype targets for a single café-restaurant branch (typically 2–6 tills). They are
not contractual SLOs.

## 5. Security and reliability review (code review + existing tests)

| Area | Finding |
|---|---|
| Tenant isolation | Every cloud query is scoped by organization, business and branch, taken from the credential, never from the request (sync, the Phase 1.5 sync-read endpoints, reports). Covered by `MultiTenantTests`, `CloudSecurityTests`. **OK** |
| Gateway authentication | ECDSA per gateway; method, path, query and body are signed; ±5 min clock window; one active credential per branch; revoked → 403 `gateway_revoked` → fenced. **OK** |
| Terminal authentication | Challenge/response with a key per terminal; the gateway certificate is pinned; tokens last 1 h but **revocation is checked on every request** and live hub sessions are closed. **OK** |
| SQLite backup integrity | Online copy, `integrity_check` on the copy, metadata read from the copy, SHA-256, sidecar written last. Validated under load (L5). **Limit:** the checksum detects corruption, not tampering. It is not a MAC or signature, and backups are not encrypted. |
| Restore / reconciliation | Lock, dry-run, branch/schema checks, refusal of newer data, forensic copy, crash marker, writes blocked until verified (Phase 1.5 tests). **OK** |
| Print-job uncertainty | Unknown outcomes become `uncertain`; never reprinted automatically (Phase 1.2/1.5 tests). **OK, not validated on physical printers** |
| WebView2 Release hardening | DevTools, context menus and accelerator keys off in Release; navigation and new windows restricted to the app origin; bridge messages checked for origin; debugging switches from environment or policy refused (E2E `release-no-remote-debugging`). **OK** |
| Error exposure | Unhandled cloud errors return a generic 503 with server-side logs (seen during the PostgreSQL restart). **OK** |

**Defects fixed in this phase: none.** No high-impact defect showed up under load or faults, so no product code was
changed. The test harness gained the reliability scenarios, process handles for memory sampling, a restart for the
private PostgreSQL test cluster, and a TCP fault relay.

## 6. Known limitations and remaining production risks

**Measurement limits**
- Single PC, loopback. Real LAN/Wi-Fi latency, packet loss, and the CPU and disk of a café PC are **not** measured.
- Terminals are simulated with the real client library; the WPF/WebView2 shell is covered only by the E2E tests.
- Up to 8 simulated terminals, about 2,000-event backlogs, a 64 MB database. Larger branches and multi-day backlogs are extrapolated, not tested.

**Production risks (to address before a pilot)**
1. **Silent network loss is detected slowly.** Up to about 30 s for terminals (SignalR keep-alive) and up to the 30 s request
   timeout for the cloud link. Writes stay safe (an unknown outcome is resolved later), but staff see a
   frozen till instead of "offline".
2. **Cloud resync after an outage** can take up to the 60 s max backoff (22 s measured). This is acceptable for reporting;
   document it for support.
3. **Manual CLI backups under heavy load** pause writes for up to about 0.5 s. Prefer the scheduled in-process backups; avoid scripting
   the CLI in a tight loop.
4. **Backups are unencrypted and unsigned.** Anyone with write access to the share can replace a backup and its
   checksum. Production needs encrypted, authenticated backups and access control on the share.
5. **Per-IP auth rate limits** (60/min) could slow reconnection after a gateway restart if many tills share one IP
   (NAT or a Wi-Fi client-isolation bridge). It is fine on a flat LAN; check on site.
6. **Drain throughput is about 136 events/s** (EF Core, one event at a time in a transaction). A full day offline (about 5–10k
   events) drains in about 1–2 min. That is acceptable but not optimized.
7. Phase 1.5 limits still apply: fencing needs the old gateway to reach the cloud; there is no merge tool for quarantined history;
   there are no payment records yet; and nothing alerts on backup or sync failures (logs and `status` only).
8. **No monitoring or alerting, no hosted cloud, no TLS from a public CA, no secret rotation, no load balancer** are in
   scope yet.
9. There are no signed installers or updates. The WebView2 runtime and .NET versions on site are not controlled.

## 7. Physical hardware test checklist (not yet done)

None of these have been performed. Each needs a written result before a pilot.

**Gateway PC**
- [ ] Target café PC (CPU/RAM/disk class), Windows Service auto-start, 8 h load at the L1 profile: latency, memory, disk.
- [ ] Power cut during writes (UPS and no UPS): SQLite integrity at boot, terminals reconnect.
- [ ] Disk nearly full: backup failure reported, gateway keeps serving.
- [ ] Backups to a real NAS/UNC share: credentials, share offline, slow link; restore on a **second PC** (DPAPI keys
      must not be usable there; `cloud-relink` path).
- [ ] Full gateway replacement on new hardware following doc 08 §7, timed end to end, including re-pairing every till.

**Network**
- [ ] Real switch/Wi-Fi: unplug cable, reboot AP, roam between APs, captive or isolated Wi-Fi, two tills behind NAT
      (rate limit).
- [ ] Internet outage of several hours with real traffic; 4G/satellite-like latency on the cloud link.
- [ ] Gateway reachable only through firewall rules as deployed (local-network filter).

**Terminals**
- [ ] Real touch POS hardware with the Release desktop app: 6+ tills, a lunch-rush scenario, sleep/resume, Windows Update
      restarts, low-memory devices.
- [ ] Unpair/re-pair and replace-terminal on a real device; a stolen device is revoked while offline.

**Printers**
- [ ] Each target thermal printer (USB, network, Bluetooth if any): paper out, cover open, power off mid-job,
      network printer unreachable. Confirm `uncertain` jobs are never reprinted automatically and that operator
      reprint works.
- [ ] Kitchen/bar routing under load (L1 profile) with physical printers: no lost or duplicated tickets.

**Cloud**
- [ ] Hosted PostgreSQL with backups/PITR, failover or restart under live sync; cloud behind a reverse proxy and public
      TLS.
- [ ] Several branches syncing at once (multi-tenant load), report queries during the drain.
