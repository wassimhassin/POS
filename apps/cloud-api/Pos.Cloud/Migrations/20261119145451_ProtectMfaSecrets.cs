using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class ProtectMfaSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "totp_key_id",
                table: "user_mfa",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_expires_at",
                table: "user_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_mfa_totp_key_id",
                table: "user_mfa",
                column: "totp_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_expires_at",
                table: "auth_tokens",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_sessions_expires_at",
                table: "user_sessions");

            migrationBuilder.DropIndex(
                name: "ix_user_mfa_totp_key_id",
                table: "user_mfa");

            migrationBuilder.DropIndex(
                name: "ix_auth_tokens_expires_at",
                table: "auth_tokens");

            migrationBuilder.DropColumn(
                name: "totp_key_id",
                table: "user_mfa");
        }
    }
}
