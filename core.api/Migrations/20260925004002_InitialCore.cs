using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AppPlatform.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "company",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company", x => x.id);
                    table.UniqueConstraint("ak_company_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "outbox_event",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    aggregate_public_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_event", x => x.id);
                    table.UniqueConstraint("ak_outbox_event_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "employee",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee", x => x.id);
                    table.UniqueConstraint("ak_employee_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_employee_company_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalSchema: "core",
                        principalTable: "company",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transport = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    destination = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbox_message_outbox_event_tenant_id_event_id",
                        columns: x => new { x.tenant_id, x.event_id },
                        principalSchema: "core",
                        principalTable: "outbox_event",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_employee_tenant_id_employee_id",
                        columns: x => new { x.tenant_id, x.employee_id },
                        principalSchema: "core",
                        principalTable: "employee",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_company_public_id",
                schema: "core",
                table: "company",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_company_tenant_id",
                schema: "core",
                table: "company",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_public_id",
                schema: "core",
                table: "employee",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id",
                schema: "core",
                table: "employee",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id_company_id",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_tenant_id_email",
                schema: "core",
                table: "employee",
                columns: new[] { "tenant_id", "email" },
                unique: true,
                filter: "email IS NOT NULL AND deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id",
                schema: "core",
                table: "outbox_event",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id_aggregate_type_aggregate_public_id_a",
                schema: "core",
                table: "outbox_event",
                columns: new[] { "tenant_id", "aggregate_type", "aggregate_public_id", "aggregate_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_status_next_attempt_at",
                schema: "core",
                table: "outbox_message",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id",
                schema: "core",
                table: "outbox_message",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id_event_id",
                schema: "core",
                table: "outbox_message",
                columns: new[] { "tenant_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_public_id",
                schema: "identity",
                table: "user",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_id",
                schema: "identity",
                table: "user",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_id_email",
                schema: "identity",
                table: "user",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_id_employee_id",
                schema: "identity",
                table: "user",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "employee_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "outbox_event",
                schema: "core");

            migrationBuilder.DropTable(
                name: "employee",
                schema: "core");

            migrationBuilder.DropTable(
                name: "company",
                schema: "core");
        }
    }
}
