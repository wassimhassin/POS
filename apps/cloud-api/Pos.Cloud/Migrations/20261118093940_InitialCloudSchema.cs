using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class InitialCloudSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "businesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_businesses", x => x.id);
                    table.UniqueConstraint("ak_businesses_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_businesses_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branches", x => x.id);
                    table.UniqueConstraint("ak_branches_organization_id_business_id_id", x => new { x.organization_id, x.business_id, x.id });
                    table.ForeignKey(
                        name: "fk_branches_businesses_organization_id_business_id",
                        columns: x => new { x.organization_id, x.business_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "branch_products",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    station_id = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branch_products", x => new { x.branch_id, x.product_id });
                    table.ForeignKey(
                        name: "fk_branch_products_branches_organization_id_business_id_branch~",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "branch_sync_states",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branch_sync_states", x => x.branch_id);
                    table.CheckConstraint("ck_branch_sync_states_last_sequence", "last_sequence >= 0");
                    table.ForeignKey(
                        name: "fk_branch_sync_states_branches_organization_id_business_id_bra~",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    public_key_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_gateway_credentials_branches_organization_id_business_id_br~",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    total_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_by_name = table.Column<string>(type: "text", nullable: false),
                    updated_by_name = table.Column<string>(type: "text", nullable: false),
                    submitted_by_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => new { x.branch_id, x.id });
                    table.CheckConstraint("ck_orders_status", "status IN ('open', 'submitted')");
                    table.CheckConstraint("ck_orders_total", "total_minor >= 0");
                    table.CheckConstraint("ck_orders_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_orders_branches_organization_id_business_id_branch_id",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provisioning_codes",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_gateway_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_codes", x => x.code_hash);
                    table.ForeignKey(
                        name: "fk_provisioning_codes_branches_organization_id_business_id_bra~",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    secret_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_keys", x => x.id);
                    table.CheckConstraint("ck_report_keys_scope", "branch_id IS NULL OR business_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_report_keys_branches_organization_id_business_id_branch_id",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_keys_businesses_organization_id_business_id",
                        columns: x => new { x.organization_id, x.business_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_events",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    event_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    gateway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_events", x => new { x.branch_id, x.sequence });
                    table.CheckConstraint("ck_sync_events_sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "fk_sync_events_branches_organization_id_business_id_branch_id",
                        columns: x => new { x.organization_id, x.business_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "organization_id", "business_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sync_events_gateway_credentials_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "gateway_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    unit_price_minor = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    station_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => new { x.branch_id, x.order_id, x.product_id });
                    table.CheckConstraint("ck_order_lines_quantity", "quantity BETWEEN 1 AND 999");
                    table.ForeignKey(
                        name: "fk_order_lines_orders_branch_id_order_id",
                        columns: x => new { x.branch_id, x.order_id },
                        principalTable: "orders",
                        principalColumns: new[] { "branch_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_branch_products_organization_id_business_id_branch_id",
                table: "branch_products",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_branch_sync_states_organization_id_business_id_branch_id",
                table: "branch_sync_states",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_branches_business_id_name",
                table: "branches",
                columns: new[] { "business_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_businesses_organization_id_name",
                table: "businesses",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_credentials_branch_id",
                table: "gateway_credentials",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_credentials_organization_id_business_id_branch_id",
                table: "gateway_credentials",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_credentials_public_key_sha256",
                table: "gateway_credentials",
                column: "public_key_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_branch_id_number",
                table: "orders",
                columns: new[] { "branch_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_organization_id_business_id_branch_id_status",
                table: "orders",
                columns: new[] { "organization_id", "business_id", "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_codes_organization_id_business_id_branch_id",
                table: "provisioning_codes",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_report_keys_organization_id_business_id_branch_id",
                table: "report_keys",
                columns: new[] { "organization_id", "business_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_events_branch_id_event_id",
                table: "sync_events",
                columns: new[] { "branch_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_events_gateway_id",
                table: "sync_events",
                column: "gateway_id");

            migrationBuilder.CreateIndex(
                name: "ix_sync_events_organization_id_business_id_branch_id_received_~",
                table: "sync_events",
                columns: new[] { "organization_id", "business_id", "branch_id", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "branch_products");

            migrationBuilder.DropTable(
                name: "branch_sync_states");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "provisioning_codes");

            migrationBuilder.DropTable(
                name: "report_keys");

            migrationBuilder.DropTable(
                name: "sync_events");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "gateway_credentials");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "businesses");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
