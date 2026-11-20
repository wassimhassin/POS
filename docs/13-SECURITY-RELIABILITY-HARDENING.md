# 13 — Security and reliability hardening (Phase 2.1C)

Scope: five issues found in or left by 2.1B (doc 12 §5). No new endpoints, no RBAC/RLS, no dashboard. Wire formats
are unchanged: gateways, terminals and the OpenAPI document see the same shapes. Decisions: ADR-019…022.

---

## 1. Gateway provisioning: a lost reply no longer burns the code

**Problem (found in 2.1B).** Two things combined:
- The gateway generated a **new key pair on every registration attempt**.
- The cloud consumed the single-use code on the first attempt that reached it.

So when the cloud committed a registration but the reply was lost (client timeout, dropped connection), every retry
came from a new identity and got 401 `invalid_code`. The gateway could never link without a new code from an operator.

**Fix, in two halves (ADR-019).**

| Side | Change |
|---|---|
| Gateway | The registration key is created **once per provisioning code** and stored DPAPI-protected in `cloud_link.pending_private_key` (gateway migration 4) *before* the first attempt. Every retry, including after a gateway restart, signs with the same key. On success the key moves to `private_key`. `cloud-link`/`cloud-relink` with a new code clears it |
| Cloud | A code already consumed is accepted again **only** from the key that consumed it (same public-key fingerprint, compared in constant time), with a valid proof of possession, for the same branch, while that credential is not revoked, and within 7 days of first use. The reply is the original registration, and nothing is written. Every other case is still 401 `invalid_code` |

Concurrent attempts are serialized by the existing `FOR UPDATE` lock on the code row. Of two racing keys, exactly
one consumes the code; every attempt from the other gets 401. A replay never revives a revoked gateway and never
revokes anything again, even for a `--replace` code.

**Compatibility.**
- Wire: unchanged request and response.
- An older gateway still sends a new key on each retry, so it still gets 401, as before.
- An older cloud with a new gateway: retries keep the same key and get 401 from the cloud, as before.

Recovery (`cloud-relink`, replacement codes, fencing of revoked gateways) is unchanged and its tests still pass.

## 2. Terminal enrollment under load

**Diagnosis.** In 2.1B a full-suite run failed in `MultiTenantTests`: a terminal enrollment hit `Pos.Core`'s 5 s
`HttpClient.Timeout` on a heavily loaded machine. The cause was the same as §1:
- The pairing code is single use.
- The first request to a cold gateway (TLS + JIT + SQLite) can exceed 5 s.
- `EnrollAsync` did not retry, and the gateway refused the code once it had been consumed.

The terminal identity was already stable (persisted key), so only replay support was missing.

**Fix.**
- **Gateway:** `EnrollTerminal` returns the existing enrollment when the same terminal id presents the same public key for a code it consumed, the terminal is active, and the code was used within the last 24 h. Any other identity still gets 401.
- **Terminal (`Pos.Core`):** enrollment uses a 15 s per-attempt timeout and up to 3 attempts on transport failures (timeout, connection error). TLS pin mismatches are never retried. Retrying is safe because of the gateway-side replay.
- **Reproduction:** `GatewayEnrollmentTests` reproduces the lost reply deterministically. A relay holds the gateway's reply past the client timeout and releases the network while the client is still waiting.

The general 5 s timeout of other LAN requests is unchanged: they are retried by the existing command engine, with
operation ids.

## 3. MFA secrets encrypted at rest

TOTP seeds are sealed with **AES-256-GCM** using application data keys from `POS_CLOUD_DATA_KEYS` (ADR-020).
`System.Security.Cryptography` only; no new dependency.

| Topic | Rule |
|---|---|
| Key ring | `id:base64(32 bytes)` entries, comma-separated. The **first** key encrypts; the others only decrypt. `Pos.Cloud data-key-new` prints a fresh entry |
| Blob | `0x01 \| len(id) \| id \| nonce(12) \| tag(16) \| ciphertext`. The associated data binds the header, the purpose (`totp`) and the **user id**: a ciphertext copied onto another user's row, or relabelled with another key id, does not decrypt |
| Column | `user_mfa.totp_key_id` (migration `ProtectMfaSecrets`, additive). Null means a legacy plaintext row from before 2.1C |
| Migration of existing rows | Lazy: a successful code re-seals the seed under the active key. Bulk: `Pos.Cloud mfa-rekey` (runtime role, batches of 200) re-seals legacy and old-key rows |
| Rotation | Put the new key first and keep the old ones after it. Restart, run `mfa-rekey`, check that `check-config` reports nothing to rekey, then remove the old keys |
| `check-config` | Error if any seed uses a key that is not configured. Warning plus `mfa_rekey_needed=N` if legacy or old-key rows remain |
| Failure | A seed that does not decrypt makes that code fail (401 `invalid_code`), with an error log naming the user id and key id, never the secret. It is never a 500 |

Recovery codes were already stored as SHA-256 hashes of 80-bit random values. Neither seeds nor codes are logged
(tests check the database rows and the process output).

## 4. Email delivery

`IAuthMailer` is now a **queue**. The request path enqueues the mail in memory and returns. One background
dispatcher delivers, in order, through one transport (ADR-021):

| Transport | Setting | Use |
|---|---|---|
| Pickup directory | `POS_CLOUD_MAIL_DIR` | Development, or a local relay agent: `.eml` files written atomically |
| SMTP | `POS_CLOUD_SMTP_HOST` (+ `_PORT` default 587, `_TLS` `starttls` default or `none`, `_USER`/`_PASSWORD`, `POS_CLOUD_MAIL_FROM`) | Production relay or provider SMTP endpoint, through `System.Net.Mail.SmtpClient` (no new dependency) |

- **Exactly one** transport must be configured. Messages name the setting, never its value.
- Plain SMTP (`_TLS=none`) is accepted **only towards a loopback relay**: one-time links must not travel in clear.
- Port 465 (implicit TLS) is refused because the client does not support it; use STARTTLS on 587.
- `POS_CLOUD_MAIL_FROM` is required with SMTP.
- **Retries:** transient failures (SMTP 421/450/451/452, network errors, timeouts) are retried after 2, 4, 8 and 16 s, 5 attempts in total. Permanent failures (5xx, TLS errors, invalid addresses) are not retried.
- **Logs:** message id, transport, attempt number and failure class or SMTP status only. Never the recipient, the body or the server's text.
- **Why a queue:** a request never waits for the relay, so its timing no longer reveals whether a known address triggered a mail (anti-enumeration). A slow relay cannot block account endpoints either.
- **Durability, honest limit:** the queue is in memory, holding at most 1000 mails, and is lost on a crash or restart. Every flow can be repeated by the user (resend verification, forgot password), and the operator can reissue an invitation. A durable outbox would store live one-time links at rest, which is why it isn't used.
- **CLI (`owner-invite`):** delivers inline with the same retries, and exits non-zero if the mail could not be delivered.

Tests use a loopback fake SMTP server (`FakeSmtpServer`). No real mail is sent. They cover AUTH, transient retries, a
permanent rejection, CLI failure, and that the password, tokens and addresses never appear in logs.

## 5. Retention and cleanup

| Data | Rule | Where |
|---|---|---|
| Sessions | Deleted 30 days after they ended: `revoked_at` or `expires_at` more than 30 days ago. A live session (not revoked, not expired) can never match | Runtime role, hourly (first run 1 min after start) and `Pos.Cloud cleanup` |
| Single-use tokens (hashes) | Deleted 7 days after expiry (consumed tokens expire within 72 h) | Same |
| Abandoned TOTP setups | Deleted 1 day after creation if never confirmed (they hold a secret for nothing) | Same |
| Security audit | **Kept at least 365 days**, default retention 400 days. Never deleted by the service: the runtime role has no `DELETE`. `Pos.Cloud audit-prune [--older-than-days N]` uses the owner role (`POS_CLOUD_MIGRATION_DB`) and refuses N < 365 | Operator, scheduled by the platform |

- Deletes run in batches of 1000, with at most 50 batches per run. A larger backlog continues on the next run (`complete=0`).
- Deletes are idempotent, so several instances can run them.
- Indexes on `user_sessions.expires_at` and `auth_tokens.expires_at` support them.
- Gateway provisioning codes are kept, because used codes back the replay window. Their retention is a later item.

---

## 6. Configuration summary (new or changed)

| Setting | Rule |
|---|---|
| `POS_CLOUD_DATA_KEYS` | **Required.** At least one `id:base64(32 bytes)`; ids lowercase/digits/dash, unique; first = active |
| `POS_CLOUD_MAIL_DIR` | Pickup transport (existing, writable, absolute) — mutually exclusive with `POS_CLOUD_SMTP_HOST` |
| `POS_CLOUD_SMTP_HOST` / `_PORT` / `_TLS` / `_USER` / `_PASSWORD` | SMTP transport; see §4 |
| `POS_CLOUD_MAIL_FROM` | Sender; required with SMTP, default `PosSaaS <no-reply@possaas.invalid>` for the pickup directory |

New CLI commands: `data-key-new`, `mfa-rekey`, `cleanup`, `audit-prune`.

## 7. Tests added

| Area | Tests |
|---|---|
| Provisioning | `GatewayProvisioningTests`: same-key replay and identical response, other key/forged proof/wrong branch refused, 8 concurrent attempts from two racing keys (one winner, one credential), replay refused after revocation or after the window, real gateway whose reply times out → restart → links with one credential |
| Enrollment | `GatewayEnrollmentTests`: raw replay, other identity, revoked terminal; lost reply through a relay → client retry → enrolled once; unreachable gateway fails cleanly within bounded time |
| MFA secrets | `MfaSecretProtectionTests`: ciphertext at rest, lazy and bulk rotation, removal of the old key, ciphertext copied to another user refused without crash, missing key = configuration error, legacy plaintext migration |
| Mail | `SmtpMailTests` (fake SMTP), `Mail_transport_and_data_keys_are_validated_without_echoing_secrets` |
| Retention | `AuthRetentionTests`: only ended sessions, expired tokens and abandoned setups removed; enabled MFA and live sessions kept; audit prune limited to the owner role and ≥ 365 days |
| Regression | `Cloud_killed_while_draining…` runs again with the default 5 s gateway timeout (the 2.1B workaround is removed) |

## 8. Remaining risks (honest)

1. **Key custody.** Data keys come from the environment (secret store). There is no KMS/HSM envelope yet: anyone who can read both the environment and the database can read the seeds.
2. **Mail queue durability.** In-memory, lost on crash (see §4). There is no bounce or complaint handling and no SPF/DKIM verification; those belong to the relay or provider.
3. **SMTP client.** `SmtpClient` is in maintenance mode upstream, with no implicit TLS and no OAuth. Behind `IMailTransport`, a provider HTTP API or MailKit can replace it without other changes.
4. **Cleanup and multiple instances.** Several instances run the same idempotent cleanup. The extra work is harmless, but there is no leader election.
5. **Replay windows.** 7 days for gateways, 24 h for terminals. A party holding the private key could fetch its own registration again during the window, which reveals nothing new.
6. Not validated on real hardware, a real SMTP provider, or a real browser.
7. **Failures seen only under heavy load (not reproduced in isolation).** When the three test assemblies ran in
   parallel on a memory-starved machine, three tests failed: a raw gateway registration whose connection was aborted
   (WSAECONNABORTED) right after cloud start, a raw enrollment request exceeding the 10 s test-client timeout, and an
   SMTP test waiting more than 20 s for the first delivery. Each passed when rerun on its own, and none failed in the
   final validation (one assembly at a time, xUnit parallelism limited to 2). Their timeouts were **not** raised.
   Instead, the SMTP waits now report the relay counters and the cloud log tail on timeout. The root cause of the
   connection abort is not established; it remains a watch item for CI.

Test-kit fixes made during validation:
- Every cloud or gateway process a test starts is killed when the test host exits. A fixture whose `InitializeAsync`
  threw used to leave its cloud running, because xUnit skips `DisposeAsync` in that case.
- Null guards in `TcpProxy.Close`.
- The Phase 1.3 upgrade test now also removes gateway schema version 4.
