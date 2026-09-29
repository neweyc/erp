using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddDataKeysAndOperatorTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "totp_secret",
                schema: "platform",
                table: "platform_user",
                newName: "totp_secret_encrypted");

            migrationBuilder.AddColumn<long>(
                name: "totp_last_used_step",
                schema: "platform",
                table: "platform_user",
                type: "bigint",
                nullable: true);

            // Every operator session that exists before this migration was signed in with a password
            // alone. MFA becomes mandatory here, so none of them may outlive it: revoked, and every
            // operator signs in again with a code. Stop the old platform.api before applying this,
            // or it can mint more password-only sessions in the gap.
            migrationBuilder.Sql(
                "UPDATE platform.platform_session SET revoked_at = now() WHERE revoked_at IS NULL;");

            migrationBuilder.CreateTable(
                name: "data_key",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kek_fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_key", x => new { x.id, x.kek_fingerprint });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_key",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "totp_last_used_step",
                schema: "platform",
                table: "platform_user");

            migrationBuilder.RenameColumn(
                name: "totp_secret_encrypted",
                schema: "platform",
                table: "platform_user",
                newName: "totp_secret");
        }
    }
}
