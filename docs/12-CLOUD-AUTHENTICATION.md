# 12 — Cloud user authentication (Phase 2.1B)

Scope: people (owners, later managers) authenticating to the cloud API. **Not** in scope: gateway/device
authentication (unchanged, doc 07), report keys (unchanged), staff PIN login (gateway, later phase), RBAC and RLS
(2.1C+), any dashboard UI. Decisions: ADR-015…018 in [DECISIONS.md](DECISIONS.md).

---

## 1. Architecture

```
Browser (dashboard, later) ──HTTPS + cookie __Host-pos_session──► Pos.Cloud
                                                                   ├─ /v1/auth/*, /v1/me*  ── scheme "Session"
Gateway  ──PosGateway signature──────────────────────────────────► ├─ /v1/gateways, /v1/sync/* ── scheme "PosGateway"
Report client ──Bearer posrk_…───────────────────────────────────► └─ /v1/reports/* ── scheme "Bearer"
```

Three authentication schemes, never interchangeable. The deny-by-default fallback policy still accepts **only**
gateway and report credentials, so a user session cannot reach any existing endpoint (tested). User endpoints name
their policy explicitly.

| Piece | Where | Notes |
|---|---|---|
| Users, password hashing, lockout, security stamp | ASP.NET Core Identity (`AddIdentityCore<CloudUser>` + EF stores on `CloudDb`, PostgreSQL) | Identity's user store only; no `SignInManager`, no Identity cookie handler, no Data Protection token providers |
| Sessions | `Identity/Sessions.cs`: `SessionService`, `SessionHandler` | Server-side rows in `user_sessions`; the cookie is an opaque random token, stored as SHA-256 |
| Flows | `Identity/AuthService.cs` | Registration, verification, login + MFA, passwords, MFA management, onboarding, audit |
| HTTP surface | `Identity/AuthApi.cs` | Endpoints, policies, origin guard; mapped into the `/v1` group |
| Single-use tokens | `auth_tokens` | Email verification (24 h), password reset (1 h), owner invitation (72 h); SHA-256 stored, consumed atomically |
| TOTP + recovery codes | `Identity/AuthSecrets.cs`, `user_mfa`, `user_recovery_codes` | RFC 6238 (SHA-1, 6 digits, 30 s, ±1 step, no replay); 10 recovery codes, hashed |
| Mail | `IAuthMailer` → in-memory queue → `IMailTransport` | Pickup directory (`POS_CLOUD_MAIL_DIR`) or SMTP (`POS_CLOUD_SMTP_*`), with retries: doc 13 §4 |
| Audit | `auth_audit_events` | Event name, user/org/session ids, IP, user agent. Append-only for the runtime role |
| Future tenancy | `organization_memberships` (org, user, role, revoked_at) | Only role `owner` exists; no permission checks use it yet |

### Tables (migration `CloudIdentity`)

`users`, `user_claims`, `user_logins`, `user_tokens` (Identity), `user_sessions`, `auth_tokens`, `user_mfa`,
`user_recovery_codes`, `organization_memberships`, `auth_audit_events`. Applied by `Pos.Cloud migrate` with the
owner role like every migration (ADR-010). After migrating, grant the runtime role:

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pos_app;
REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM pos_app;
REVOKE UPDATE, DELETE ON auth_audit_events FROM pos_app;   -- checked by check-config
```

---

## 2. Endpoints

All under `/v1`, JSON, error envelope `{code, message}` (ADR-013). Responses carry `Cache-Control: no-store`.

| Endpoint | Auth | Result |
|---|---|---|
| `POST /auth/register` `{email, password}` | anonymous, mail rate limit | 202 `verification_sent`: same answer whether or not the address has an account |
| `POST /auth/verify-email` `{token}` | anonymous | 200 `verified`, or 400 `invalid_token` |
| `POST /auth/resend-verification` `{email}` | anonymous, mail rate limit | always 202 |
| `POST /auth/login` `{email, password}` | anonymous | 200 `signed_in` or `mfa_required` (5-minute second-factor session); 401 `invalid_credentials`; 403 `email_not_verified` (only with the right password) |
| `POST /auth/login/mfa` `{code}` or `{recoveryCode}` | second-factor session | 200 `signed_in` (+ `recoveryCodesRemaining` after a recovery code); 401 `invalid_code` |
| `POST /auth/logout` | any session | 204 |
| `POST /auth/password/forgot` `{email}` | anonymous, mail rate limit | always 202 |
| `POST /auth/password/reset` `{token, newPassword}` | anonymous | 200; ends every session, lifts lockout; no automatic sign-in |
| `POST /auth/onboarding/accept` `{token}` | full session | 200 `{organizationId, role: owner}`; 403 `mfa_required` until MFA is on |
| `GET /me` | session (incl. enrolment) | id, email, verification, MFA state, memberships |
| `GET /me/sessions`, `DELETE /me/sessions/{id}`, `POST /me/sessions/revoke-others` | session (incl. enrolment) | Another user's session id is 404, like an unknown one |
| `POST /me/password` `{currentPassword, newPassword}` | full session | Other sessions end; this one gets a new key |
| `POST /me/mfa/totp` | session (incl. enrolment) | `{sharedKey, otpAuthUri}` (inactive until confirmed); 409 if already on |
| `POST /me/mfa/totp/confirm` `{code}` | session (incl. enrolment) | 10 recovery codes, shown once; other sessions end |
| `POST /me/mfa/recovery-codes` `{code}` | full session | Replaces all codes |
| `POST /me/mfa/disable` `{password, code}` | full session | 403 `mfa_required_for_role` for owners |

**Policies.** `user`: second factor done or not required. `user-enrolling`: also allows a *privileged* user who has
no MFA yet, but only for identity, sessions and MFA setup. `user-mfa-challenge`: password verified, factor pending.
`user-any-session`: logout. A session in the wrong state gets 403 `mfa_required`, and no session gets 401.

**Privileged** means having a live organization membership. Privileged users must have MFA (requirement 3). The rule is
enforced in two places: accepting an owner invitation requires MFA, and a member without MFA (for example, after an
operator removes it) is confined to enrolment.

---

## 3. Security decisions

**Passwords.** Identity's PBKDF2 hasher. 12–128 characters, ≥ 5 distinct characters, no composition rules
(NIST SP 800-63B), must not contain the address' local part. Breached-password screening is not implemented (§5).

**Sessions (ADR-015).**
- The cookie is `__Host-pos_session`: `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`.
- The value is 32 random bytes; only its SHA-256 is stored.
- Lifetimes: idle 2 h, absolute 12 h, second-factor step 5 min.
- The key rotates every hour on use. The previous key is accepted for 1 minute so concurrent requests survive.
- Every privilege or credential change creates a new session and ends the others: login, MFA completion, MFA on/off, password change, invitation acceptance.
- Logging in from a client that already has a session revokes that session (no fixation).
- Each session records the Identity security stamp. A password reset or change renews the stamp, which kills every session even if a revocation were missed.
- Revocation is immediate because sessions are checked server-side on every request.

**Single-use tokens (ADR-016).**
- Random 256-bit tokens, stored hashed, bound to a purpose and expiry, and consumed by a conditional update (a concurrent second use fails).
- Links put the token in the URL **fragment** (`/auth/verify-email#token=…`), so it never reaches server logs, proxies or `Referer`.
- Identity's Data Protection token providers are not used: that avoids a key ring to persist and share, and gives true single use.

**MFA (ADR-017).**
- TOTP per RFC 6238, verified in constant time over ±1 step.
- The last accepted step is stored and updated conditionally, so a code cannot be replayed, even concurrently.
- Second-factor failures share Identity's lockout counter with password failures (5 failures → 15-minute lockout). The counter is reset only by a *complete* sign-in, so knowing the password does not buy fresh guesses.
- Lockout during the second step revokes the pending session.
- Recovery codes are 80-bit, hashed, single use, and accepted in any case or with any separator.

**Anti-enumeration.**
- Unknown address, wrong password and locked account all give the same 401 `invalid_credentials`. An unknown address is checked against a dummy hash so timing matches.
- Register, resend and forgot always answer 202 with the same body.
- Registering an existing address sends that address a notice (verified) or a new link (unverified). It never creates a second account or changes the password.
- `email_not_verified` is only returned after the correct password.

**Abuse.**
- Per IP: 20/min on credential endpoints, 10/min on mail-sending endpoints (429 `rate_limited` + `Retry-After`).
- Per account: Identity lockout, plus at most 3 security mails per 15 minutes.

**CSRF.**
- `SameSite=Strict` is the first layer.
- For unsafe methods under `/v1/auth` and `/v1/me`, a request with an `Origin` other than `POS_CLOUD_PUBLIC_ORIGIN`, or with `Sec-Fetch-Site: cross-site`, gets 403 `origin_mismatch`. Login is covered too, which stops login CSRF.
- Strict JSON bodies are required (no form posts).

**Onboarding (ADR-018).** There is no public way to create an organization or a privileged account.

1. The platform operator runs `Pos.Cloud owner-invite --org <id> --email <address>`. This emails a 72-hour, single-use invitation and prints only `invite=sent` and the expiry, never the token.
2. The invitee registers, verifies the address and enables MFA.
3. The invitee accepts. Acceptance requires a verified address equal to the invited one. A wrong account gets the generic `invalid_token`. A missing MFA gets `mfa_required` without consuming the invitation.

**Audit and logs.**
- Events: `user_registered`, `register_existing_address`, `email_verified`, `login_succeeded`, `login_failed`, `login_email_not_verified`, `account_locked_out`, `mfa_challenge_succeeded`/`_failed`, `recovery_code_used`, `mfa_setup_started`, `mfa_enabled`, `mfa_disabled`, `recovery_codes_regenerated`, `password_changed`, `password_reset_requested`, `password_reset`, `logout`, `session_revoked`, `other_sessions_revoked`, `owner_invited`, `owner_invite_accepted`.
- No passwords, tokens, codes or email addresses are stored in audit rows or written to logs. A test checks the database rows and the process output.
- The runtime role cannot update or delete audit rows (`check-config` refuses otherwise).

**Tenant isolation.** User sessions carry no tenant scope and open no tenant data. Memberships are recorded but
grant nothing until RBAC (2.1C). Gateway and report-key isolation are unchanged.

---

## 4. Configuration

| Setting | Rule |
|---|---|
| `POS_CLOUD_PUBLIC_ORIGIN` | Required. `https://host[:port]`, no path or trailing slash. Used in email links and the origin check |
| `POS_CLOUD_MAIL_DIR` or `POS_CLOUD_SMTP_HOST` | Exactly one mail transport (doc 13 §4, §6). `POS_CLOUD_DATA_KEYS` is required too (doc 13 §3) |
| Database (`check-config`) | Additionally: the runtime role must not have `UPDATE`/`DELETE` on `auth_audit_events` |

Both settings are validated by `run` and `check-config` like the others (doc 11 §4); messages never echo values.
The OpenAPI document adds the `Session` cookie scheme; the snapshot is regenerated and drift-checked.

---

## 5. Remaining limitations (honest)

Items 1–3 and 10 of the 2.1B list (pickup-only mail, plaintext TOTP seeds, no cleanup, non-retryable gateway
registration) were fixed in Phase 2.1C: see [doc 13](13-SECURITY-RELIABILITY-HARDENING.md).

4. **No breached-password check** (k-anonymity range API or local list), and **no WebAuthn/passkeys** (TOTP only).
5. **Rate limits are per process**, in memory. Several instances need a shared limiter or an edge limiter.
   `RemoteIpAddress` assumes no reverse proxy: behind a proxy, forwarded headers must be configured with known proxies.
6. **No RBAC/RLS.** Memberships exist, but no endpoint authorizes by them yet. There is no membership management
   API and no "remove owner" flow apart from the database.
7. **Email change and account deletion** are not implemented.
8. Lockout lets an attacker lock a known account for 15 minutes (the usual trade-off). Per-IP limits slow this down
   but do not prevent it.
9. Not validated against a real browser or dashboard (none exists yet). The tests use an HTTP client with a cookie jar.
---

## 6. Tests

`Pos.Cloud.Tests`: `UserAuthTests`, `UserMfaTests`, plus additions to `ApiFoundationTests`/`ConfigValidationTests`.
Each test runs a real cloud process on a private PostgreSQL database. Each simulated client has its own cookie jar
and its own loopback source address (`127.0.0.x`), so rate limits stay independent. TOTP codes come from an
independent implementation in the test kit. Expiry is tested by moving timestamps in the database.

| Area | Test |
|---|---|
| Registration and verification, existing-address behaviour, weak password/email, hashed storage | `Registration_requires_email_verification_and_never_reveals_existing_accounts` |
| Cookie attributes, listing, revoking own vs. another user's session, re-login, rotation + grace, idle/absolute expiry, revoke-others, logout | `Sessions_are_secure_cookies_listable_revocable_expiring_and_rotated` |
| Lockout, reset (weak, single use, ends sessions, lifts lockout), password change | `Lockout_password_reset_and_password_change` |
| Enumeration (login/forgot/resend), mail budget, unauthenticated access, CSRF origin, per-IP brute-force limit | `Anonymous_endpoints_resist_enumeration_cross_origin_requests_and_brute_force` |
| Gateway / report key / session separation, no public org creation | `User_sessions_gateway_signatures_and_report_keys_never_substitute_for_each_other` |
| Audit trail content, no secrets in rows or logs, append-only | `Security_events_are_audited_without_secrets_and_logs_hold_no_tokens` |
| TOTP enrolment, challenge, replay, recovery codes (single use, regeneration), disable | `Totp_enrolment_challenge_replay_protection_and_recovery_codes` |
| Second-factor brute force → lockout, pending session killed | `Failed_second_factors_lock_the_account_and_kill_the_pending_session` |
| Invitation-only owner onboarding, MFA gate, wrong account, expiry, owners keep MFA, enrolment confinement | `Owner_onboarding_is_invitation_only_and_requires_mfa` |
| New settings required/validated; audit table append-only | `User_authentication_settings_are_required_and_validated`, `The_security_log_must_be_append_only_for_the_runtime_role` |
