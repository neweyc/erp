using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAppRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "access_version",
                schema: "identity",
                table: "user",
                type: "bigint",
                nullable: false,
                // Edited from the generated 0L: existing users start where new ones do (User.AccessVersion = 1).
                defaultValue: 1L);

            migrationBuilder.AddColumn<string[]>(
                name: "app_roles",
                schema: "identity",
                table: "user",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_version",
                schema: "identity",
                table: "user");

            migrationBuilder.DropColumn(
                name: "app_roles",
                schema: "identity",
                table: "user");
        }
    }
}
