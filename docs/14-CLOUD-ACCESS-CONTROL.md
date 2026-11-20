# 14 — Cloud access control: roles, scopes and memberships (Phase 2.1D)

Scope: authorization of cloud users inside an organization, plus the minimal membership API (list, invite, accept,
change role/scope, revoke). The phase adds no organization/business/branch CRUD, no PostgreSQL RLS, no dashboard and
no staff management. Gateway/device authentication, report keys and the planned offline staff PIN model are
unchanged. Decisions: ADR-023…026.

---

## 1. Model

| Table | Change | Purpose |
|---|---|---|
| `organization_memberships` | + `business_id`, `branch_id`, `version`, `updated_at`; role check `owner/admin/manager/viewer` | One membership per (organization, user): **one role, one scope** |
| `membership_invitations` | new | Pending invitations: SHA-256 of the token, invited address (normalized), role, scope, inviter, expiry, acceptance or revocation |
| `auth_audit_events` | + `target_user_id`, `details` (≤ 200 chars) | Who changed whom, and how. Ids, role and scope only |

**Scope.** Three kinds, the same shape as report keys:
- organization: both ids null;
- business: `business_id` set;
- branch: `branch_id` set, together with the branch's `business_id`.

The database enforces it:
- Composite foreign keys `(organization_id, business_id)` → businesses and `(organization_id, business_id, branch_id)` → branches, so a scope can never name another organization's business or branch, even through an application bug.
- A branch scope must name its business.
- Owners and admins are always organization-wide (check constraint).

Migration `CloudAccessControl` is additive. Existing owner memberships become organization-wide owners at version 1.

## 2. Permission catalog and roles

| Permission | Allows |
|---|---|
| `organization.read` | See the organization's businesses and branches **inside one's scope** |
| `members.read` | List active members whose scope lies inside one's scope |
| `members.invite` | Invite (and list or cancel invitations) for roles below one's own, inside one's scope |
| `members.manage` | Change the role and scope of, or revoke, members below one's own role, inside one's scope |

| Role | Rank | Permissions | Allowed scope |
|---|---|---|---|
| Owner | 4 | all | organization |
| Admin | 3 | all | organization |
| Manager | 2 | `organization.read`, `members.read` | organization, business or branch |
| Viewer | 1 | `organization.read` | organization, business or branch |

Permission codes are stable data (API responses, audit details). Later phases add codes (catalog, staff, reports…) and
map them to roles here. The offline staff permissions delivered to gateways (doc 10, ADR-006) are a separate model.

## 3. Enforcement

- **Deny by default.** Every route under `/v1/organizations/{organizationId}` passes `OrganizationAccessFilter`:
  1. The organization comes from the path. The caller's access comes from **their membership row**, read from the database on every request. The request body never supplies a tenant or a scope that is trusted.
  2. If the caller has no live membership (or their address is unverified): **404** `not_found`. The answer is the same for an organization that does not exist, so nothing can be probed.
  3. If the endpoint declares no permission: **403**, plus an error log. A route cannot be forgotten open.
  4. If the caller lacks the declared permission: **403** `forbidden`. A refused write permission is audited as `permission_denied`.
- **Session policy.** Only a full session with MFA done (`user` policy) reaches these routes. Every member counts as privileged, so MFA is mandatory and a member without it is confined to MFA enrolment (unchanged from ADR-017).
- **Privilege escalation.**
  - A role can only be granted to someone, or changed or revoked on a member, by a strictly higher role. Owners are the exception: they can act on any role, including owners.
  - The target scope must lie inside the actor's scope.
  - An admin therefore cannot create, promote, demote or revoke an owner or another admin, cannot promote themselves, and cannot revoke themselves.
  - These checks are evaluated **again under the organization lock** (§5), so an actor demoted by a concurrent request is stopped.
- **Scopes in requests.** A business or branch id is resolved inside the path's organization. An id from another organization looks exactly like an unknown one (400 `invalid_scope`). A branch id fixes its own business.
- **Scoped reads.** Branches, members and invitations are filtered by the caller's scope: a branch manager sees their branch and the members scoped to it; a business viewer sees that business's branches.

## 4. API (session cookie; origin check and `no-store` as for `/v1/auth` and `/v1/me`)

| Method and path | Permission | Result |
|---|---|---|
| `GET /v1/organizations/{id}/access` | `organization.read` | Caller's role, scope, permissions |
| `GET /v1/organizations/{id}/branches` | `organization.read` | Branches inside the caller's scope |
| `GET /v1/organizations/{id}/members` | `members.read` | Active members inside the caller's scope (user id, email, role, scope, version) |
| `PATCH /v1/organizations/{id}/members/{userId}` | `members.manage` | `{role, expectedVersion, businessId?, branchId?}` → member; 409 `version_conflict` or `last_owner` |
| `DELETE /v1/organizations/{id}/members/{userId}` | `members.manage` | 204; 409 `last_owner` |
| `GET /v1/organizations/{id}/invitations` | `members.invite` | Pending invitations inside the caller's scope |
| `POST /v1/organizations/{id}/invitations` | `members.invite` | `{email, role, businessId?, branchId?}` → 202 `{invitationId, expiresAt}`; 409 `already_member` / `too_many_invitations` |
| `DELETE /v1/organizations/{id}/invitations/{invitationId}` | `members.invite` | 204 |
| `POST /v1/invitations/accept` | signed in (full session) | `{token}` → `{organizationId, role, scope}` |

**Rate limits.**
- Invitations use the per-IP mail limit (10/min).
- Member changes use the auth limit (20/min).
- An organization holds at most 100 pending invitations.

`GET /v1/me` keeps its shape; its memberships now show the new role names. The owner onboarding by CLI
(`owner-invite`, `/v1/auth/onboarding/accept`) is unchanged and remains the only way to create the first owner.

## 5. Invitations, changes and concurrency

- **Invitations.**
  - The token is 256 random bits, mailed only and stored as SHA-256. The link is `/auth/join#token=…`.
  - Valid for 72 h and accepted once, by a **verified account with the invited address and enabled MFA**. A refusal for missing MFA does not consume the invitation.
  - A new invitation to the same address replaces the previous one.
  - At acceptance the **inviter's current authority is checked again**. If the inviter was demoted or removed, the link dies (audited `reason=inviter_authority`).
  - A revoked member can be invited back: the same row is reactivated with a new version.
- **Serialization.**
  - Every write (invite, cancel, accept, change, revoke) runs in a transaction that first takes `SELECT … FROM organizations … FOR NO KEY UPDATE`. That lock does not block foreign-key checks from sync traffic.
  - Inside the lock, the actor's membership is re-read and the target is checked against it.
- **Last owner.** Demoting or revoking an owner is refused (409 `last_owner`, audited `last_owner_protected`) when no other active owner remains. Two owners demoting each other, or both stepping down at once, leave exactly one owner (tested).
- **Optimistic concurrency.** Changes carry `expectedVersion`. A stale version gets 409 `version_conflict` and nothing is written.
- **Immediate effect.**
  - Permissions are never cached: the next request of the affected user is evaluated against the new row.
  - In addition, every change, revocation or acceptance renews the user's security stamp and **revokes all of their sessions** (`permission_changed`, `membership_revoked`, `privilege_granted`). The affected user must sign in again with MFA.
  - If an actor changes their own membership, their cookie is cleared as well.

## 6. Audit

| Event | Actor (`user_id`) | `target_user_id` | `details` (examples) |
|---|---|---|---|
| `member_invited` | inviter | — | `invitation=…;role=manager;scope=branch:…` |
| `member_invitation_revoked` | actor or invitee | — | `invitation=…;reason=revoked\|superseded\|inviter_authority` |
| `member_invitation_accepted` | new member | new member | `invitation=…;role=…;scope=…` |
| `member_changed` | actor | member | `from=manager/business:…;to=viewer/branch:…;version=3` |
| `member_revoked` | actor | member | `role=…;scope=…;version=…` |
| `permission_denied` | actor | member or — | `permission=members.manage`, or the refused change |
| `last_owner_protected` | actor | owner | — |

The log stays append-only for the runtime role (`check-config` verifies it). Addresses, tokens and codes never
appear (tests assert that no `details` value contains `@`). Retention is unchanged (≥ 365 days, ADR-022).
Invitations are deleted 30 days after expiry by the hourly cleanup (`cleanup` prints `invitations=N`).

## 7. Configuration

No new settings. `check-config` now also fails when the runtime role cannot `SELECT`/`INSERT` an application table
(for example after a migration added `membership_invitations` without re-running the deploy-time `GRANT`). The error
names the table, never a credential.

## 8. Tests (`AccessControlTests`)

| Requirement | Test |
|---|---|
| Catalog, scoped reads, deny by default, 401/404 | `Roles_grant_their_catalog_permissions_inside_their_scope_and_nothing_else` |
| Cross-tenant denial; foreign scopes; database composite keys | `Other_organizations_and_their_businesses_or_branches_are_unreachable` |
| Unauthorized role changes / escalation | `Nobody_can_grant_or_act_on_a_role_at_or_above_their_own` |
| Branch-scope isolation | Scoped branches and members in the first test; foreign and mismatched branch scopes in the second |
| Last-owner protection | `The_last_active_owner_can_be_neither_demoted_nor_revoked` |
| Concurrent modifications | `Concurrent_membership_changes_are_serialized`: mutual demotion, simultaneous step-down, same-version edits |
| Invitation expiry, replay, binding, supersession, cancellation, inviter authority | `Invitations_are_single_use_expire_and_bind_to_the_address_and_the_inviters_authority` |
| Session revocation and immediate effect | `Permission_changes_apply_to_the_next_request_and_end_the_members_sessions` |
| Configuration validation | `Check_config_requires_the_runtime_role_to_reach_the_access_tables` |

## 9. Limitations (honest)

1. **One role and one scope per membership.** A manager of two branches needs a business scope or two organizations. A multi-assignment table can come later without changing the permission checks.
2. **No PostgreSQL RLS yet.** Isolation is application-level: the filter, scoped queries and composite keys. RLS on `pos_app` remains planned (ADR-003).
3. **Business- or branch-scoped administrators do not exist.** Only owners and admins can invite or manage, and both are organization-wide. Rules for managers who invite inside their branch are implemented in the checks but not exposed by any role yet.
4. **Admins cannot leave on their own.** An owner revokes them; there is no "leave organization" endpoint.
5. **Invitation mails are queued in memory** (ADR-021). An invitation lost in a crash must be re-sent, which supersedes the old one.
6. **Rate limits are per process and per IP** (unchanged, doc 12 §5).
7. **Revoking sessions is account-wide.** A change in one organization signs the user out of every organization; this is deliberate.
8. Not validated with a real browser or dashboard.
