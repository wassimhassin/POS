using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class CloudStructureManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "organizations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "businesses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "branches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "branches",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Narrower pre-tenant lookups (doc 16 §5): a report key answers only for its secret's hash, and memberships
            // only for the user of a live session. Deploy step: GRANT EXECUTE ON ALL FUNCTIONS ... TO pos_app (again).
            migrationBuilder.Sql("""
                DROP FUNCTION app_report_key(uuid);
                CREATE FUNCTION app_report_key(p_key uuid, p_secret_sha256 text)
                RETURNS TABLE (organization_id uuid, business_id uuid, branch_id uuid)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT k.organization_id, k.business_id, k.branch_id
                  FROM public.report_keys k
                  WHERE k.id = p_key AND k.revoked_at IS NULL AND k.secret_sha256 = p_secret_sha256
                $$;
                REVOKE ALL ON FUNCTION app_report_key(uuid, text) FROM PUBLIC;

                DROP FUNCTION app_user_memberships(uuid);
                CREATE FUNCTION app_session_memberships(p_session uuid, p_user uuid)
                RETURNS TABLE (organization_id uuid, role text, business_id uuid, branch_id uuid, version integer, created_at timestamp with time zone)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT m.organization_id, m.role::text, m.business_id, m.branch_id, m.version, m.created_at
                  FROM public.organization_memberships m
                  WHERE m.user_id = p_user AND m.revoked_at IS NULL
                    AND EXISTS (SELECT 1 FROM public.user_sessions s WHERE s.id = p_session AND s.user_id = p_user AND s.revoked_at IS NULL)
                $$;
                REVOKE ALL ON FUNCTION app_session_memberships(uuid, uuid) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION app_session_memberships(uuid, uuid);
                CREATE FUNCTION app_user_memberships(p_user uuid)
                RETURNS TABLE (organization_id uuid, organization_name text, role text, business_id uuid, branch_id uuid, version integer, created_at timestamp with time zone)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT m.organization_id, o.name::text, m.role::text, m.business_id, m.branch_id, m.version, m.created_at
                  FROM public.organization_memberships m JOIN public.organizations o ON o.id = m.organization_id
                  WHERE m.user_id = p_user AND m.revoked_at IS NULL
                $$;
                REVOKE ALL ON FUNCTION app_user_memberships(uuid) FROM PUBLIC;

                DROP FUNCTION app_report_key(uuid, text);
                CREATE FUNCTION app_report_key(p_key uuid)
                RETURNS TABLE (organization_id uuid, business_id uuid, branch_id uuid, secret_sha256 text, revoked boolean)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$
                  SELECT k.organization_id, k.business_id, k.branch_id, k.secret_sha256::text, k.revoked_at IS NOT NULL
                  FROM public.report_keys k WHERE k.id = p_key
                $$;
                REVOKE ALL ON FUNCTION app_report_key(uuid) FROM PUBLIC;
                """);

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "version",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "version",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "version",
                table: "branches");
        }
    }
}
