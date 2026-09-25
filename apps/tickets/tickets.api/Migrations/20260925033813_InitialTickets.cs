using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class InitialTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tickets");

            migrationBuilder.CreateTable(
                name: "outbox_event",
                schema: "tickets",
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
                name: "ticket",
                schema: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assignee_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_display_name = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "tickets",
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
                        principalSchema: "tickets",
                        principalTable: "outbox_event",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id",
                schema: "tickets",
                table: "outbox_event",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id_aggregate_type_aggregate_public_id_a",
                schema: "tickets",
                table: "outbox_event",
                columns: new[] { "tenant_id", "aggregate_type", "aggregate_public_id", "aggregate_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_status_next_attempt_at",
                schema: "tickets",
                table: "outbox_message",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id",
                schema: "tickets",
                table: "outbox_message",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id_event_id",
                schema: "tickets",
                table: "outbox_message",
                columns: new[] { "tenant_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_public_id",
                schema: "tickets",
                table: "ticket",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_tenant_id",
                schema: "tickets",
                table: "ticket",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_tenant_id_status",
                schema: "tickets",
                table: "ticket",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "tickets");

            migrationBuilder.DropTable(
                name: "ticket",
                schema: "tickets");

            migrationBuilder.DropTable(
                name: "outbox_event",
                schema: "tickets");
        }
    }
}
