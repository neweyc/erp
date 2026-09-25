using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantProvisioningKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provisioning_key",
                schema: "platform",
                table: "tenant",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_provisioning_key",
                schema: "platform",
                table: "tenant",
                column: "provisioning_key",
                unique: true,
                filter: "provisioning_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_provisioning_key",
                schema: "platform",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "provisioning_key",
                schema: "platform",
                table: "tenant");
        }
    }
}
