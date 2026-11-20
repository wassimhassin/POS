# 11 — CI and API foundations (Phase 2.1A)

**Scope:** cross-cutting foundations from doc 10 §10, milestone 2.1, first slice. There is no authentication,
user management, row-level security or business feature yet. Existing endpoints, paths, wire formats and tests
are unchanged.

| Area | What was added |
|---|---|
| CI | `.github/workflows/ci.yml` runs on push/PR: UI checks, .NET Debug and Release tests, then E2E. `reliability.yml` is manual only. |
| PostgreSQL in CI | Test kits also find PostgreSQL through `PGBIN` (set on GitHub's Windows runners). CI fails early and clearly if the binaries are missing. |
| Errors | Every error body, including the ones the framework produces (unknown route, wrong method, wrong media type, rate limit), is the `CloudError` envelope `{ code, message }`. |
| Versioning | All routes are mapped through one `/v1` route group. Other versions are unknown routes. |
| OpenAPI | First-party generator (`Microsoft.AspNetCore.OpenApi` 10.0.15). There is a committed snapshot `docs/api/cloud-v1.openapi.json` and a test that fails when it drifts. The live endpoint is opt-in. |
| Configuration | `run` validates every setting before listening and reports all problems at once, without values. `Pos.Cloud check-config` validates without starting. |

---

## 1. Running the checks

### Locally (Windows, PowerShell, repository root)

| What | Command | Time on the dev PC |
|---|---|---|
| Everything (UI, .NET Debug and Release, E2E) | `.\scripts\test.ps1` | dominated by the cloud suite, twice |
| Without E2E | `.\scripts\test.ps1 -SkipE2E` | |
| UI only | `npm ci --prefix apps/pos-web; npm run typecheck --prefix apps/pos-web; npm test --prefix apps/pos-web` | < 1 min |
| One .NET project | `dotnet test apps/cloud-api/Pos.Cloud.Tests -c Debug` (build first) | Cloud ≈ 10 min |
| Like CI (projects one at a time) | `dotnet test PosSaaS.slnx -c Release --no-build -m:1` | |
| One E2E | `node tests/e2e/cloud-sync.mjs` (after `.\scripts\build.ps1`) | |
| Reliability (opt-in) | `$env:POS_RELIABILITY = "1"; dotnet test apps/cloud-api/Pos.Cloud.Tests -c Release --filter "FullyQualifiedName~ReliabilityTests"` | see doc 09 |
| Regenerate the OpenAPI snapshot | `Pos.Cloud openapi --out docs/api/cloud-v1.openapi.json` (or run the tests with `POS_UPDATE_OPENAPI=1`) | seconds |

PostgreSQL: the cloud tests and `cloud-sync.mjs` start **private throwaway clusters**. They run `initdb`/`pg_ctl` under the
current user on a free port, in a folder under `%TEMP%`. An installed PostgreSQL service is never used or modified.
Binaries are found in this order: `POS_PG_BIN`, then `PGBIN`, then the newest `C:\Program Files\PostgreSQL\<n>\bin`.
If none is found, the tests **fail**; they do not skip silently.

On a low-memory machine, run the suites one after another (as above) rather than in parallel; see doc 09.

### In CI (`.github/workflows/ci.yml`)

| Job | Runner | Steps | Timeout |
|---|---|---|---|
| `ui` | `ubuntu-latest` | `npm ci`, typecheck, Vitest, Vite build | 15 min |
| `dotnet` (matrix Debug, Release; runs in parallel) | `windows-2025` | restore → build → `dotnet test -m:1` (projects one at a time), TRX results uploaded **on failure only** (7 days) | 60 min |
| `e2e` | `windows-2025` | Runs only if `ui` and `dotnet` pass. It checks PostgreSQL and the WebView2 runtime, runs `build.ps1` (Debug), builds Release, then runs every `tests/e2e/*.mjs` sequentially | 45 min |

How resource use is kept down:
- `concurrency` cancels the older run for the same branch or PR.
- Workflow permissions are read-only (`contents: read`).
- NuGet and npm caches are used.
- E2E runs only after the cheaper jobs succeed.
- Artifacts are uploaded only on failure.
- The reliability scenarios are excluded: `POS_RELIABILITY` is forced empty, so they skip.

`reliability.yml` is **manual** (`workflow_dispatch`). It runs L1–L5/R1–R4 on a Release build and uploads the JSONL
results. Numbers from a shared hosted runner are not comparable with doc 09's dev-PC figures.

### CI limitations (honest status)

- **The workflows have not run on GitHub yet.** This phase only commits them, without pushing. They are validated
  as YAML, and every command they run was run locally. Runner-specific assumptions are checked by explicit steps
  that fail with a clear message:
  - `PGBIN` with `pg_ctl.exe` on `windows-2025`;
  - the WebView2 runtime registry key;
  - an interactive desktop session for the WPF E2E tests.
- The desktop E2E tests drive WebView2 through remote debugging and a real window. Hosted runners usually allow
  this, but it is unverified here. If the runner image lacks WebView2 or a desktop session, the `e2e` job fails at
  that step; the other jobs are unaffected.
- `print-queue.mjs` uses the Windows spooler and simulated printers only; there is no hardware in CI (doc 09 §7 still applies).
- Timing-sensitive tests (reconnects, ±5 min signatures, recovery) were stable on the dev PC. Slower shared runners may
  expose flakiness; first runs should be watched.
- Third-party actions are pinned to major versions (`@v4`). Pinning to commit SHAs and adding dependency/secret scanning
  are follow-ups (doc 10 §9, supply chain).
- Hosted Windows runners are billed at a higher rate than Linux. Only the UI job runs on Linux, because every .NET test
  project targets `net10.0-windows` (the cloud project itself is cross-platform).

---

## 2. Error envelope and versioning (contract)

Every error response has `Content-Type: application/json` and **exactly** this body:

```json
{ "code": "snake_case_code", "message": "Texte lisible (français)." }
```

**The shape is frozen.** Gateways already in the field deserialize `CloudError` with strict JSON (unknown members are
rejected), and they recognize revocation by its `code`. An added member would make every error unreadable to them,
including `gateway_revoked`. New information must go into new codes or headers, or into a `/v2` envelope. The same
rule applies to **every response shape** consumed by gateways: adding a member is a breaking change for them (see ADR-013).

| Status | Code(s) | Produced by |
|---|---|---|
| 400 | `invalid_request` (malformed or unknown JSON member), `invalid_query`, `invalid_batch`, sync rejection codes | Framework / endpoints |
| 401 | `unauthorized`, `invalid_code` | Authentication challenge, registration |
| 403 | `gateway_revoked` | Revoked gateway credential |
| 404 | `not_found` | Unknown route **when authenticated**, out-of-scope or non-existent resource |
| 405 | `method_not_allowed` | Known path, wrong method (authenticated) |
| 409 | `branch_mismatch`, `gateway_already_active`, `key_already_registered` | Registration |
| 413 | `payload_too_large` | Body > 2 MB |
| 415 | `unsupported_media_type` | Body not `application/json` (authenticated) |
| 429 | `rate_limited` + `Retry-After` header | Registration rate limit (10/min/IP) |
| 503 | `unavailable` | Unhandled error: logged server-side, never returned |

Deny by default still applies: an **anonymous** request to an unknown route, a wrong method or a wrong media type gets
`401 unauthorized`, so routes cannot be enumerated without credentials. Messages never echo input.

**Versioning:** every route is mapped through one `/v1` group (`CloudApi.VersionPrefix`). Paths are unchanged:
`/v1/gateways/register`, `/v1/sync/events`, `/v1/sync/state` and `/v1/reports/...`. A breaking change adds a `/v2` group beside it, and
`/v1` is kept while gateways that use it are in the field (doc 10 §5–6). Today `/v2/...` is simply an unknown route.

---

## 3. OpenAPI

- **Generator:** `Microsoft.AspNetCore.OpenApi` 10.0.15 (first party, the same version as the ASP.NET Core runtime). It brings
  `Microsoft.OpenApi` 2.x as its only transitive dependency. The output is OpenAPI 3.1.
- **Document:** [`docs/api/cloud-v1.openapi.json`](api/cloud-v1.openapi.json). It covers 9 operations with operation ids
  and tags (Gateways, Sync, Reports), request and response schemas, the error envelope per status, and two security
  schemes: `PosGateway` (signed `Authorization` header) and `ReportKey` (bearer).
- **Excluded by construction:**
  - No `servers`, so no host or listen address.
  - No examples, credentials or database details; only the public contract types from `Pos.Sync.Contracts`, no EF entities.
  - Debug fault injection has no endpoint, so it cannot appear.
  - A test fails if the document contains `secret`, `password`, `pos_app`, `fault`, `connection`, private-key terms,
    `localhost` or `127.0.0.1`.
- Schemas set `additionalProperties: false`, matching the strict server and gateway deserializers.
- **Generation:** `Pos.Cloud openapi --out <file>`. It starts the host on a no-op server, as the framework's build-time
  tool does, so no socket is opened and no database is contacted. The output is identical between runs and between Debug and Release.
- **Drift check:** `Committed_OpenAPI_document_matches_the_code_and_exposes_no_internals` regenerates the document and
  compares it with the committed file. Any endpoint or contract change must update the snapshot in the same commit, which makes
  the contract change visible in review.
- **Live endpoint:** `GET /openapi/v1.json` is served **only** with `Pos.Cloud run --openapi` (anonymous, same
  document; a test checks that they match). It is off by default, so a production host does not advertise its surface.
- **Next:** generate TypeScript types for `packages/contracts` from this file when the dashboard starts (doc 10 §4, ADR-008).

---

## 4. Configuration validation

`Pos.Cloud run` validates **all** settings before opening a socket and prints every problem at once (exit code 2).
`Pos.Cloud check-config --listen … --certificate …` performs the same checks plus database checks, then exits.
That makes it suitable as a deploy/readiness step.

| Setting | Rule | Severity |
|---|---|---|
| `--listen` | Required; `https://<ip>:<port>` (no plain HTTP) | Error |
| `POS_CLOUD_DB` | Required; a readable connection string with Host, Database and Username | Error |
| `POS_CLOUD_DB` vs `POS_CLOUD_MIGRATION_DB` | The runtime must not use the schema-owner username | Error |
| `POS_CLOUD_DB` | `Include Error Detail` enabled | Warning |
| `--certificate` + `POS_CLOUD_CERT_PASSWORD` | File exists; password ≥ 12 characters; opens with that password; has a private key; currently valid | Error |
| Certificate | Expires in < 30 days | Warning |
| Database (`check-config` only) | Reachable as the runtime role; no pending migration; role is neither superuser nor allowed `CREATE` on `public` | Error |
| `POS_CLOUD_PUBLIC_ORIGIN`, `POS_CLOUD_MAIL_DIR` (2.1B) | https origin without path; existing writable absolute directory | Error |
| `POS_CLOUD_SMTP_*`, `POS_CLOUD_MAIL_FROM`, `POS_CLOUD_DATA_KEYS` (2.1C) | Exactly one mail transport; SMTP rules in doc 13 §4; at least one valid data key | Error (values never echoed) |
| Database (`check-config`, 2.1B) | Runtime role has no `UPDATE`/`DELETE` on `auth_audit_events` | Error |

Messages name the setting at fault and **never its value**. Parser and driver messages, which can quote a connection
string, a host or a user, are not forwarded; only the exception class is shown. Tests check this with a marker secret
in an unreadable connection string, a wrong certificate password and an unreachable database.

This stays inside ADR-011: configuration still comes only from code defaults and environment variables. Settings
added later (Identity keys, entitlement signing, public URLs) must be added to `CloudSettings` together with their rules.

---

## 5. Tests added

| Test | Checks |
|---|---|
| `Framework_failures_use_the_error_envelope` | 401/404/405/415/400 bodies are exactly `{code, message}` and deserialize as `CloudError` with the gateways' strict options. Anonymous unknown routes and `/v2` get 401; existing routes are unchanged |
| `Rate_limited_requests_use_the_error_envelope_and_say_when_to_retry` | 429 `rate_limited` + `Retry-After` |
| `OpenApi_is_not_served_unless_enabled` | 401 anonymous, 404 authenticated |
| `Committed_OpenAPI_document_matches_the_code_and_exposes_no_internals` | Snapshot equality, no forbidden terms, 9 `/v1` operations, the right scheme on each, envelope schema |
| `OpenApi_can_be_served_on_request_and_is_the_committed_contract` | `run --openapi` serves the committed document |
| `A_correct_configuration_passes` | `check-config` → `config=ok` |
| `Every_missing_setting_is_reported_at_once_by_name` | All four problems in one run |
| `Secrets_are_never_echoed` | Unreadable string, wrong certificate password and unreachable database: the secret is never printed |
| `The_runtime_must_not_use_the_schema_owner_or_hold_DDL_rights` | Owner username refused; `CREATE` on `public` detected, then accepted once revoked |
| `Run_refuses_an_invalid_configuration_before_listening` | `run` exits 2 with the list |

### Existing tests made deterministic

Running the suite with the added tests, which bring more parallel processes, exposed three timing races in existing
tests. In each case the product behaved correctly; the tests assumed an ordering that load can change. I fixed the
tests and left the product code alone, which is also what CI's slower shared runners need:

| Test | Race | Fix |
|---|---|---|
| `Replacement_hardware_needs_an_explicit_replacement_code_and_fences_the_old_gateway` | After revocation, the old gateway is fenced by the sync of its *first* write. Under load that happened before the test's second write (add item), which was then correctly refused | Exactly one write after revocation |
| `A_revoked_gateway_is_refused_and_keeps_its_unsent_events` | Same pattern | Same fix |
| `Cloud_killed_while_draining_a_large_backlog_then_restarted_ends_with_each_event_once` | The kill was meant to land mid-drain, but a loaded machine could find the whole backlog already drained (observed: 67/67). The safety assertion, acknowledged ≤ committed, always held | The gateway syncs through the existing test relay, holding each reply 150 ms, and the kill happens at the first committed event: about 66 batches (≥ 10 s) remain, against 50 ms polling. Both assertions now print their values |

---

## 6. Next steps (rest of milestone 2.1)

1. Push and watch the first CI runs; fix any runner-specific issues. Then pin actions to SHAs and add dependency and secret scanning.
2. Identity: users, organization memberships, roles and permissions catalog, branch-scoped assignments (ADR-006).
   *User authentication was done in 2.1B (doc 12); roles, permissions and assignments remain.*
3. PostgreSQL RLS on `pos_app` (ADR-003), checked in `check-config`.
4. Management API for organizations, businesses and branches with an audit log, following §2's envelope, cursor paging and the
   OpenAPI snapshot.
5. Cross-tenant negative tests for every new endpoint.
