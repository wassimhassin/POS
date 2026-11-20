using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <summary>
    /// Phase 2.1E (doc 15): PostgreSQL row-level security on every tenant-owned table, a transaction-local tenant
    /// context, and the few narrow SECURITY DEFINER lookups that legitimately run before a tenant is known. Data and
    /// columns are unchanged; Down restores the previous state exactly.
    /// </summary>
    public partial class CloudRowLevelSecurity : Migration
    {
        /// <summary>Tables whose rows carry <c>organization_id</c> (organizations: <c>id</c>).</summary>
        private static readonly string[] OrganizationTables =
        [
            "businesses", "branches", "gateway_credentials", "provisioning_codes", "branch_sync_states", "sync_events",
            "orders", "branch_products", "report_keys", "organization_memberships", "membership_invitations",
        ];

        private static readonly string[] Functions =
        [
            "app_current_organization()", "app_enter_tenant(uuid)", "app_gateway_credential(uuid)", "app_report_key(uuid)",
            "app_provisioning_code_organization(text)", "app_gateway_key_registered(text)", "app_invitation_organization(bytea)",
            "app_user_memberships(uuid)", "app_purge_expired_invitations(timestamp with time zone, integer)",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------ tenant context (transaction-local)
            // The organization is valid only inside the transaction that set it: app_enter_tenant stores it with
            // set_config(..., is_local => true) together with the transaction's start time; a value left at session
            // level (or by an earlier transaction on a pooled connection) never matches and reads as "no tenant".
            migrationBuilder.Sql("""
                CREATE FUNCTION app_current_organization() RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE
                AS $$
                  SELECT CASE
                    WHEN current_setting('app.tenant_txn', true) = ((extract(epoch FROM transaction_timestamp()) * 1000000)::bigint)::text
                    THEN nullif(current_setting('app.organization_id', true), '')::uuid
                  END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION app_enter_tenant(p_organization uuid) RETURNS void
                LANGUAGE plpgsql VOLATILE SET search_path = pg_catalog, public
                AS $$
                DECLARE
                  v_current uuid := public.app_current_organization();
                BEGIN
                  IF p_organization IS NULL THEN
                    RAISE EXCEPTION 'tenant context requires an organization' USING ERRCODE = '22023';
                  END IF;
                  IF v_current IS NOT NULL AND v_current <> p_organization THEN
                    RAISE EXCEPTION 'tenant context already set to another organization in this transaction' USING ERRCODE = '42501';
                  END IF;
                  PERFORM set_config('app.organization_id', p_organization::text, true);
                  PERFORM set_config('app.tenant_txn', ((extract(epoch FROM transaction_timestamp()) * 1000000)::bigint)::text, true);
                END
                $$;
                """);

            // ------------------------------------------------------------ narrow pre-tenant lookups (SECURITY DEFINER)
            // Each answers one question about one row identified by a server-side secret or id, and returns only what
            // the caller needs to authenticate and then enter that row's tenant. None lists or scans tenants.
            migrationBuilder.Sql("""
                CREATE FUNCTION app_gateway_credential(p_gateway uuid)
                RETURNS TABLE (organization_id uuid, business_id uuid, branch_id uuid, public_key bytea, revoked boolean)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT g.organization_id, g.business_id, g.branch_id, g.public_key, g.revoked_at IS NOT NULL
                  FROM public.gateway_credentials g WHERE g.id = p_gateway
                $$;

                CREATE FUNCTION app_report_key(p_key uuid)
                RETURNS TABLE (organization_id uuid, business_id uuid, branch_id uuid, secret_sha256 text, revoked boolean)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT k.organization_id, k.business_id, k.branch_id, k.secret_sha256::text, k.revoked_at IS NOT NULL
                  FROM public.report_keys k WHERE k.id = p_key
                $$;

                CREATE FUNCTION app_provisioning_code_organization(p_code_hash text) RETURNS uuid
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$ SELECT c.organization_id FROM public.provisioning_codes c WHERE c.code_hash = p_code_hash $$;

                CREATE FUNCTION app_gateway_key_registered(p_fingerprint text) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$ SELECT EXISTS (SELECT 1 FROM public.gateway_credentials g WHERE g.public_key_sha256 = p_fingerprint) $$;

                CREATE FUNCTION app_invitation_organization(p_token_hash bytea) RETURNS uuid
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$ SELECT i.organization_id FROM public.membership_invitations i WHERE i.token_hash = p_token_hash $$;

                CREATE FUNCTION app_user_memberships(p_user uuid)
                RETURNS TABLE (organization_id uuid, organization_name text, role text, business_id uuid, branch_id uuid, version integer, created_at timestamp with time zone)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT m.organization_id, o.name::text, m.role::text, m.business_id, m.branch_id, m.version, m.created_at
                  FROM public.organization_memberships m JOIN public.organizations o ON o.id = m.organization_id
                  WHERE m.user_id = p_user AND m.revoked_at IS NULL
                $$;

                CREATE FUNCTION app_purge_expired_invitations(p_cutoff timestamp with time zone, p_limit integer) RETURNS integer
                LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  WITH doomed AS (
                    SELECT i.id FROM public.membership_invitations i WHERE i.expires_at < p_cutoff LIMIT least(greatest(p_limit, 0), 1000)),
                  gone AS (
                    DELETE FROM public.membership_invitations i USING doomed d WHERE i.id = d.id RETURNING 1)
                  SELECT count(*)::integer FROM gone
                $$;
                """);

            // Functions are executable by PUBLIC by default: only the roles granted at deploy time may call them.
            foreach (var function in Functions)
            {
                migrationBuilder.Sql($"REVOKE ALL ON FUNCTION {function} FROM PUBLIC;");
            }

            // ------------------------------------------------------------ policies (deny without a tenant)
            migrationBuilder.Sql("""
                ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON organizations
                  USING (id = app_current_organization()) WITH CHECK (id = app_current_organization());
                """);
            foreach (var table in OrganizationTables)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY tenant_isolation ON {table}
                      USING (organization_id = app_current_organization()) WITH CHECK (organization_id = app_current_organization());
                    """);
            }

            // Order lines are keyed by (branch, order): visible exactly when their order is (itself tenant-filtered).
            migrationBuilder.Sql("""
                ALTER TABLE order_lines ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON order_lines
                  USING (EXISTS (SELECT 1 FROM orders o WHERE o.branch_id = order_lines.branch_id AND o.id = order_lines.order_id))
                  WITH CHECK (EXISTS (SELECT 1 FROM orders o WHERE o.branch_id = order_lines.branch_id AND o.id = order_lines.order_id));
                """);

            // The security log: account-level rows (no organization) stay readable and writable by the service; rows of an
            // organization only inside that tenant. No UPDATE/DELETE policy at all (append-only, as the grants already are).
            migrationBuilder.Sql("""
                ALTER TABLE auth_audit_events ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_audit_read ON auth_audit_events FOR SELECT
                  USING (organization_id IS NULL OR organization_id = app_current_organization());
                CREATE POLICY tenant_audit_append ON auth_audit_events FOR INSERT
                  WITH CHECK (organization_id IS NULL OR organization_id = app_current_organization());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY tenant_audit_read ON auth_audit_events;
                DROP POLICY tenant_audit_append ON auth_audit_events;
                ALTER TABLE auth_audit_events DISABLE ROW LEVEL SECURITY;
                DROP POLICY tenant_isolation ON order_lines;
                ALTER TABLE order_lines DISABLE ROW LEVEL SECURITY;
                DROP POLICY tenant_isolation ON organizations;
                ALTER TABLE organizations DISABLE ROW LEVEL SECURITY;
                """);
            foreach (var table in OrganizationTables)
            {
                migrationBuilder.Sql($"DROP POLICY tenant_isolation ON {table}; ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            foreach (var function in Functions)
            {
                migrationBuilder.Sql($"DROP FUNCTION {function};");
            }
        }
    }
}
