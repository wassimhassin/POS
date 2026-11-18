using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class OneActiveGatewayPerBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "replaces_active",
                table: "provisioning_codes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_credentials_one_active_per_branch",
                table: "gateway_credentials",
                column: "branch_id",
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_gateway_credentials_one_active_per_branch",
                table: "gateway_credentials");

            migrationBuilder.DropColumn(
                name: "replaces_active",
                table: "provisioning_codes");
        }
    }
}
