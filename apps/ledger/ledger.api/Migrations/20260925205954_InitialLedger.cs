using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AppPlatform.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "account",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.id);
                    table.UniqueConstraint("ak_account_tenant_id_company_id_id", x => new { x.tenant_id, x.company_id, x.id });
                    table.CheckConstraint("ck_account_code", "code ~ '^[0-9A-Za-z][0-9A-Za-z.-]{0,19}$'");
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entry_sequence",
                schema: "ledger",
                columns: table => new
                {
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    last_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entry_sequence", x => new { x.tenant_id, x.company_id, x.fiscal_year });
                });

            migrationBuilder.CreateTable(
                name: "journal_entry",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reverses_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    line_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entry", x => x.id);
                    table.UniqueConstraint("ak_journal_entry_tenant_id_company_id_id", x => new { x.tenant_id, x.company_id, x.id });
                    table.CheckConstraint("ck_journal_entry_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_journal_entry_fiscal_year", "fiscal_year = extract(year from entry_date)");
                    table.CheckConstraint("ck_journal_entry_line_count", "line_count >= 2");
                    table.CheckConstraint("ck_journal_entry_number", "number > 0");
                    table.ForeignKey(
                        name: "fk_journal_entry_journal_entry_tenant_id_company_id_reverses_e",
                        columns: x => new { x.tenant_id, x.company_id, x.reverses_entry_id },
                        principalSchema: "ledger",
                        principalTable: "journal_entry",
                        principalColumns: new[] { "tenant_id", "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_event",
                schema: "ledger",
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
                name: "journal_line",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_line", x => x.id);
                    table.CheckConstraint("ck_journal_line_amount_negatable", "amount_minor > -9223372036854775808");
                    table.CheckConstraint("ck_journal_line_amount_nonzero", "amount_minor <> 0");
                    table.ForeignKey(
                        name: "fk_journal_line_account_tenant_id_company_id_account_id",
                        columns: x => new { x.tenant_id, x.company_id, x.account_id },
                        principalSchema: "ledger",
                        principalTable: "account",
                        principalColumns: new[] { "tenant_id", "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_line_journal_entry_tenant_id_company_id_entry_id",
                        columns: x => new { x.tenant_id, x.company_id, x.entry_id },
                        principalSchema: "ledger",
                        principalTable: "journal_entry",
                        principalColumns: new[] { "tenant_id", "company_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "ledger",
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
                        principalSchema: "ledger",
                        principalTable: "outbox_event",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_public_id",
                schema: "ledger",
                table: "account",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_tenant_id",
                schema: "ledger",
                table: "account",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_tenant_id_company_id_code",
                schema: "ledger",
                table: "account",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_entity_type_entity_id",
                schema: "ledger",
                table: "audit_log",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_occurred_at",
                schema: "ledger",
                table: "audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_entry_sequence_tenant_id",
                schema: "ledger",
                table: "entry_sequence",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_public_id",
                schema: "ledger",
                table: "journal_entry",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_tenant_id",
                schema: "ledger",
                table: "journal_entry",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_tenant_id_company_id_fiscal_year_number",
                schema: "ledger",
                table: "journal_entry",
                columns: new[] { "tenant_id", "company_id", "fiscal_year", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_tenant_id_company_id_reverses_entry_id",
                schema: "ledger",
                table: "journal_entry",
                columns: new[] { "tenant_id", "company_id", "reverses_entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_tenant_id_idempotency_key",
                schema: "ledger",
                table: "journal_entry",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_tenant_id_reverses_entry_id",
                schema: "ledger",
                table: "journal_entry",
                columns: new[] { "tenant_id", "reverses_entry_id" },
                unique: true,
                filter: "reverses_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_tenant_id",
                schema: "ledger",
                table: "journal_line",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_tenant_id_account_id",
                schema: "ledger",
                table: "journal_line",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_tenant_id_company_id_account_id",
                schema: "ledger",
                table: "journal_line",
                columns: new[] { "tenant_id", "company_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_tenant_id_company_id_entry_id",
                schema: "ledger",
                table: "journal_line",
                columns: new[] { "tenant_id", "company_id", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_tenant_id_entry_id_line_number",
                schema: "ledger",
                table: "journal_line",
                columns: new[] { "tenant_id", "entry_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id",
                schema: "ledger",
                table: "outbox_event",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_tenant_id_aggregate_type_aggregate_public_id_a",
                schema: "ledger",
                table: "outbox_event",
                columns: new[] { "tenant_id", "aggregate_type", "aggregate_public_id", "aggregate_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_status_next_attempt_at",
                schema: "ledger",
                table: "outbox_message",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id",
                schema: "ledger",
                table: "outbox_message",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id_event_id",
                schema: "ledger",
                table: "outbox_message",
                columns: new[] { "tenant_id", "event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "entry_sequence",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_line",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "account",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_entry",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "outbox_event",
                schema: "ledger");
        }
    }
}
