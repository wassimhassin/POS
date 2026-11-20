# 15 — Cloud tenant isolation in PostgreSQL: row-level security (Phase 2.1E)

Scope: defence in depth under the application-level isolation of ADR-003 and docs 10/14.
- Every organization-owned table is protected by a PostgreSQL row-level security (RLS) policy.
- The service sees and writes a tenant's rows only inside a transaction bound to that tenant.
- Privileged platform work uses a separate role, and only a few narrow lookups run before a tenant is known.

The phase does not change API wire contracts. It adds no organization/business/branch API, dashboard or staff/PIN change.
Decisions: ADR-027…030.

---

## 1. Inventory

| Class | Tables | Protection |
|---|---|---|
| **Organization-owned** | `organizations` (by `id`), `businesses`, `branches`, `gateway_credentials`, `provisioning_codes`, `branch_sync_states`, `sync_events`, `orders`, `branch_products`, `report_keys`, `organization_memberships`, `membership_invitations` | Policy `tenant_isolation`: `organization_id = app_current_organization()` for both USING and WITH CHECK |
| Organization-owned through a parent | `order_lines` (keyed by branch + order) | `tenant_isolation`: the line's order must be visible (and `orders` is itself filtered) |
| Security log | `auth_audit_events` | `tenant_audit_read` (SELECT) and `tenant_audit_append` (INSERT): account rows (no organization) or rows of the current tenant. There is no UPDATE/DELETE policy, so the log is append-only, as its grants already are |
| Account-level (not tenant data) | `users`, `user_claims`, `user_logins`, `user_tokens`, `user_sessions`, `auth_tokens`, `user_mfa`, `user_recovery_codes` | No RLS. They are reached only by the session's user id (doc 12) |

The composite foreign keys (docs 10/14) are unchanged and still refuse a row whose org → business → branch chain is
inconsistent. PostgreSQL checks foreign keys without RLS, so both layers apply. `check-config` refuses any public table
that is in neither list, so a new table cannot ship unclassified.

**Access paths:**

| Path | Tenant comes from | Before the tenant is known |
|---|---|---|
| Gateway sync (`POST /v1/sync/events`, `GET /v1/sync/state`, `GET /v1/sync/events`) | The verified gateway credential | `app_gateway_credential(id)`, used to verify the signature |
| Gateway registration | The provisioning code | `app_provisioning_code_organization(hash)`; `app_gateway_key_registered(fingerprint)` (keys are unique across all tenants; it answers yes/no only) |
| Reports | The verified report key | `app_report_key(id, secret hash)` (2.1F: answers only for the matching secret) |
| Organization API (docs 14, 16) | The caller's verified membership (never the path alone) | `app_session_memberships(session, user)` (2.1F: a live session of that user) |
| Invitation acceptance | The invitation token | `app_invitation_organization(token hash)` |
| Owner onboarding | The server-side onboarding token row | none |
| `/v1/me`, MFA gate | none (account-level) | `app_session_memberships(session, user)` |
| Hourly cleanup | none (all tenants) | `app_purge_expired_invitations(cutoff, limit)` (delete-only, at most 1000 per call) |
| Platform CLI (provisioning) | none: privileged role (§4) | n/a |

## 2. Tenant context: transaction-local, server-derived

- `CloudDb.BeginTenantAsync(organizationId)` opens a transaction and calls `app_enter_tenant(id)`. That function stores
  the id with `set_config(..., is_local => true)`, together with the transaction's start time.
- `app_current_organization()` returns the id only when that stored start time equals `transaction_timestamp()`.
  Consequences:
  - The context ends with the transaction (commit, rollback, failure, or the connection being disposed with the
    transaction open), so **a pooled connection cannot carry it into another request**.
  - A value left at session level, for example by an accidental `SET` or by an earlier transaction, never matches.
  - Entering a tenant outside a transaction block lasts a single statement.
- Without a context, the policies see **no rows** and accept **no inserts** (deny by default).
- `app_enter_tenant` refuses `NULL` (22023). It also refuses a second, different organization in the same transaction
  (42501). A non-UUID value fails closed with an error (22P02).
- The organization id always comes from an authenticated principal: gateway credential, report key, verified
  membership, invitation token or onboarding token. Request bodies never supply it, and the organization in an API
  path is used only after the caller's membership there has been verified.
- Reads use the same mechanism: the endpoint filter `InTenantOf(...)` (sync GETs and reports) and
  `InTenantAsync(...)` (membership reads).

## 3. Privileged lookups (SECURITY DEFINER)

There are seven lookups.
- Each one is owned by the schema owner (who is exempt from RLS) and pins `search_path = pg_catalog, public`, with
  every object schema-qualified.
- Each answers one question about **one row identified by a server-side id, hash or secret**, and returns only what the
  caller needs to authenticate and then enter that row's tenant. None lists or scans tenants.

`app_current_organization` and `app_enter_tenant` are ordinary invoker functions. The migration revokes `EXECUTE` on
every one of the nine functions from `PUBLIC`; the deploy step grants it to the runtime role only.

## 4. Roles

| Role | Variable | Used by | RLS |
|---|---|---|---|
| Schema owner | `POS_CLOUD_MIGRATION_DB` | `migrate`, `audit-prune` (never platform commands since 2.1F) | Exempt as table owner (RLS is not FORCEd, so migrations and the definer functions work) |
| Platform role `pos_platform` (required since 2.1F) | `POS_CLOUD_ADMIN_DB` | `org-create`, `business-create`, `branch-create`, `gateway-code`, `gateways`, `gateway-revoke`, `report-key`, `owner-invite` | BYPASSRLS, not owner or superuser, exact grants ([doc 16 §4](16-CLOUD-STRUCTURE-MANAGEMENT.md)); verified by every command |
| Runtime `pos_app` | `POS_CLOUD_DB` | The service, `cleanup`, `mfa-rekey`, `check-config` | Fully subject: not superuser, no BYPASSRLS, owns nothing |

`run` and `check-config` refuse a runtime connection whose user equals the migration or admin user.

**Audit of tenant administration.** Each platform command writes one `auth_audit_events` row in the target
organization, containing ids only and never codes, keys, tokens or addresses:
- `platform_organization_created`, `platform_business_created`, `platform_branch_created`;
- `platform_gateway_code_issued` (branch, replace flag, validity);
- `platform_report_key_created` (key id, scope);
- `platform_gateway_revoked`.

Gateways add `gateway_registered` and `gateway_replaced`. `owner_invited` is unchanged.

## 5. Deployment

After `Pos.Cloud migrate` (schema owner), run once per deployment, and again after every migration:

```sql
GRANT USAGE ON SCHEMA public TO pos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pos_app;
REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM pos_app;
REVOKE UPDATE, DELETE ON auth_audit_events FROM pos_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO pos_app;     -- new in 2.1E
```

Since Phase 2.1F the dedicated platform role is **required**, with exactly the grants printed by
`Pos.Cloud platform-grants --role pos_platform` (doc 16 §4). The broader grants this section described in 2.1E are now
refused: run `REVOKE ALL ON ALL TABLES IN SCHEMA public FROM pos_platform;` before applying the printed ones.

Then run `Pos.Cloud check-config …` as the runtime role. Exit 0 means ready. In addition to the earlier checks, it now
verifies:
- the role has no BYPASSRLS and owns no object in `public`;
- no `app.organization_id` is preset at role or database level;
- every table is classified, and every tenant and audit table has RLS enabled with exactly the expected policies (an
  extra permissive policy is refused);
- every tenant function is executable;
- an end-to-end probe sees no tenant row without a context.

## 6. Recovery procedures

| Symptom | Cause | Action |
|---|---|---|
| `check-config`: "fonction(s) inaccessibles" / service 503 on every request | `GRANT EXECUTE` not run after the migration | Run the §5 grants |
| "sécurité par ligne absente ou modifiée sur X" | Policy dropped or RLS disabled by hand, or an extra policy added | As the owner: `ALTER TABLE X ENABLE ROW LEVEL SECURITY`; drop foreign policies; recreate `tenant_isolation` exactly as in migration `CloudRowLevelSecurity` |
| "ni d'organisation ni de compte : T" | A new table without classification | Add it to `Tenancy.TenantTables` (with a policy in a migration) or to `AccountTables` |
| "le rôle applicatif possède …" / "BYPASSRLS" | The runtime role owns a table or bypasses RLS | `ALTER TABLE … OWNER TO <schema owner>` then re-run §5 (ownership transfer drops the role's explicit grants); `ALTER ROLE pos_app NOBYPASSRLS` |
| "contexte d'organisation est prédéfini" | `ALTER ROLE/DATABASE … SET app.organization_id` | `ALTER ROLE pos_app RESET app.organization_id` / `ALTER DATABASE … RESET app.organization_id` |
| Platform command: "opération de plateforme refusée" | `POS_CLOUD_ADMIN_DB` unset, the runtime or owner role, or wrong grants | Use the dedicated platform role with the `platform-grants` output (doc 16 §4) |
| Emergency rollback | A defect in the policies | `Pos.Cloud migrate` cannot go down by itself. As the owner, apply the migration's Down script (it disables RLS and drops the functions), deploy the previous build, and keep application-level isolation as the only layer meanwhile |

## 7. Tests (`RowLevelSecurityTests`, plus all existing suites)

| Requirement | Test |
|---|---|
| No context: no reads, no writes, inserts refused | `Without_a_tenant_context_the_runtime_role_reads_and_writes_no_tenant_row` |
| Cross-tenant SELECT/INSERT/UPDATE/DELETE inside a tenant | `Inside_a_tenant_other_tenants_rows_cannot_be_selected_inserted_updated_or_deleted` |
| Missing, forged, stale, session-level or second context | `A_missing_forged_stale_or_session_level_context_grants_nothing` |
| Pool reuse (one physical connection, commit/rollback/failure) | `A_pooled_connection_never_carries_a_tenant_into_the_next_use` |
| Concurrent organizations (real gateway sync and reports at once, plus interleaved raw transactions on a small pool) | `Concurrent_requests_of_different_organizations_stay_isolated` |
| Runtime role privileges, migration ownership, definer functions, PUBLIC execute | `The_runtime_role_cannot_bypass_or_alter_row_level_security_and_does_not_own_the_schema` |
| `check-config` detects disabled, extra-policy, missing-grant, unclassified, owned, preset and BYPASSRLS cases | `Check_config_detects_disabled_weakened_or_bypassed_row_level_security` |
| Platform role separation and audit | `Platform_operations_need_a_privileged_role_and_every_tenant_administration_is_audited` |
| Composite foreign keys still enforced | `MultiTenantTests.The_database_itself_rejects_rows_whose_tenant_chain_is_inconsistent` (now inside the claimed tenant) |
| Gateway sync, reports, Identity, RBAC | The full Debug, Release and E2E suites |

## 8. Limitations and residual risks (honest)

1. **The application sets its own context.** RLS protects against a forgotten tenant filter or a query reaching the
   wrong rows. It does not protect against code that deliberately calls `app_enter_tenant` for another organization,
   nor against SQL injection, which could do the same. The context is set in one place (`BeginTenantAsync`) from
   authenticated principals only.
2. **The pre-tenant lookups are callable by the runtime role for any id.**
   - (Narrowed in 2.1F, doc 16 §5.) In 2.1E, `app_user_memberships` listed the memberships of any user id; it now
     needs a live session of that user.
   - In 2.1E, `app_report_key` returned a key's SHA-256 secret hash (2.1F: it needs the hash and returns none) (the secret itself is 256 bits, so the hash cannot be
     inverted).
   - `app_gateway_credential` returns a public key.

   An attacker running SQL as `pos_app` could therefore learn membership structure by user id.
3. **Unique constraints are global.** A gateway key fingerprint or a reused branch id can reveal that a value exists in
   another tenant (409). These are side channels, not data access.
4. **Account-level tables have no RLS** (users, sessions, tokens, MFA). They are not tenant data, but a defect in the
   account code is not contained by RLS.
5. **The platform role is powerful.** It defaults to the schema owner (DDL), so a dedicated BYPASSRLS DML role is
   recommended (§5). Its CLI actions are audited, but `gateways` (a read-only listing) is not.
6. **No FORCE ROW LEVEL SECURITY.** The owner is exempt by design (migrations, definer functions).
7. **The deploy GRANT is a manual step.** `check-config` detects a missing grant, but does not fix it.
8. Tested on PostgreSQL 18 in throwaway local clusters only. Not yet run in CI or on a managed PostgreSQL service.
