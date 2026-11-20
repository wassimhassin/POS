using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class CloudAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_role",
                table: "organization_memberships");

            migrationBuilder.AddColumn<Guid>(
                name: "branch_id",
                table: "organization_memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "business_id",
                table: "organization_memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "organization_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "organization_memberships",
                type: "integer",
                nullable: false,
                defaultValue: 1); // existing memberships start at version 1

            migrationBuilder.AddColumn<string>(
                name: "details",
                table: "auth_audit_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_user_id",
                table: "auth_audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "membership_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_invitations", x => x.id);
                    table.CheckConstraint("ck_membership_invitations_org_wide_roles", "role NOT IN ('owner', 'admin') OR business_id IS NULL");
                    table.CheckConstraint("ck_membership_invitations_role", "role IN ('owner', 'admin', 'manager', 'viewer')");
                    table.CheckConstraint("ck_membership_invitations_scope", "branch_id IS NULL OR business_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_membership_invitations_branches_organization_id_business_id~",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_membership_invitations_businesses_organization_id_business_~",
                        columns: x => new { x.organization_id, x.business_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_membership_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_membership_invitations_users_invited_by",
                        column: x => x.invited_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_organization_id_business_id_branch~",
                table: "organization_memberships",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_org_wide_roles",
                table: "organization_memberships",
                sql: "role NOT IN ('owner', 'admin') OR business_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_role",
                table: "organization_memberships",
                sql: "role IN ('owner', 'admin', 'manager', 'viewer')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_scope",
                table: "organization_memberships",
                sql: "branch_id IS NULL OR business_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_events_organization_id_occurred_at",
                table: "auth_audit_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_invitations_expires_at",
                table: "membership_invitations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_membership_invitations_invited_by",
                table: "membership_invitations",
                column: "invited_by");

            migrationBuilder.CreateIndex(
                name: "ix_membership_invitations_organization_id_business_id_branch_id",
                table: "membership_invitations",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_invitations_organization_id_normalized_email",
                table: "membership_invitations",
                columns: new[] { "organization_id", "normalized_email" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_invitations_token_hash",
                table: "membership_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_organization_memberships_branches_organization_id_business_~",
                table: "organization_memberships",
                columns: new[] { "organization_id", "business_id", "branch_id" },
                principalTable: "branches",
                principalColumns: new[] { "organization_id", "business_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_organization_memberships_businesses_organization_id_busines~",
                table: "organization_memberships",
                columns: new[] { "organization_id", "business_id" },
                principalTable: "businesses",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_organization_memberships_branches_organization_id_business_~",
                table: "organization_memberships");

            migrationBuilder.DropForeignKey(
                name: "fk_organization_memberships_businesses_organization_id_busines~",
                table: "organization_memberships");

            migrationBuilder.DropTable(
                name: "membership_invitations");

            migrationBuilder.DropIndex(
                name: "ix_organization_memberships_organization_id_business_id_branch~",
                table: "organization_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_org_wide_roles",
                table: "organization_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_role",
                table: "organization_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_scope",
                table: "organization_memberships");

            migrationBuilder.DropIndex(
                name: "ix_auth_audit_events_organization_id_occurred_at",
                table: "auth_audit_events");

            migrationBuilder.DropColumn(
                name: "branch_id",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "version",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "details",
                table: "auth_audit_events");

            migrationBuilder.DropColumn(
                name: "target_user_id",
                table: "auth_audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_role",
                table: "organization_memberships",
                sql: "role IN ('owner')");
        }
    }
}
