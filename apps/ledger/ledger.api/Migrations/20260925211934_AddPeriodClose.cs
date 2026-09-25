using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class AddPeriodClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "books",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    closed_through = table.Column<DateOnly>(type: "date", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_books", x => x.id);
                    table.UniqueConstraint("ak_books_tenant_id_company_id", x => new { x.tenant_id, x.company_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_books_public_id",
                schema: "ledger",
                table: "books",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_books_tenant_id",
                schema: "ledger",
                table: "books",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_journal_entry_books_tenant_id_company_id",
                schema: "ledger",
                table: "journal_entry",
                columns: new[] { "tenant_id", "company_id" },
                principalSchema: "ledger",
                principalTable: "books",
                principalColumns: new[] { "tenant_id", "company_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                -- Closing only moves forward. There is no reopen: a closed period stays exactly what
                -- was reported for it, and a mistake found in it is corrected in an open one.
                CREATE FUNCTION ledger.assert_close_moves_forward() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                BEGIN
                  IF (NEW.tenant_id, NEW.company_id) <> (OLD.tenant_id, OLD.company_id)
                     OR (OLD.closed_through IS NOT NULL
                         AND (NEW.closed_through IS NULL OR NEW.closed_through < OLD.closed_through)) THEN
                    RAISE EXCEPTION 'books for company % are closed through %: a close cannot move back',
                      OLD.company_id, OLD.closed_through
                      USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NEW;
                END
                $$;

                CREATE TRIGGER books_close_moves_forward
                  BEFORE UPDATE ON ledger.books
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_close_moves_forward();

                -- No entry may be dated in a closed period.
                --
                -- FOR SHARE on the company's books row: a close is an UPDATE of that row, so a post and a
                -- close cannot interleave. While a post is in flight the close waits for it; while a close
                -- is in flight the post waits, then reads the new closing date and is refused. Without the
                -- lock, a post that checked just before a close committed would land inside the period
                -- the close had just shut. The foreign key guarantees the row exists.
                CREATE FUNCTION ledger.assert_entry_in_open_period() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                DECLARE
                  closed date;
                BEGIN
                  SELECT closed_through INTO closed FROM ledger.books
                   WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
                   FOR SHARE;

                  IF closed IS NOT NULL AND NEW.entry_date <= closed THEN
                    RAISE EXCEPTION 'the books are closed through %: an entry cannot be dated %', closed, NEW.entry_date
                      USING ERRCODE = 'check_violation', CONSTRAINT = 'ledger_period_closed';
                  END IF;

                  RETURN NEW;
                END
                $$;

                CREATE TRIGGER journal_entry_in_open_period
                  BEFORE INSERT ON ledger.journal_entry
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_in_open_period();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER journal_entry_in_open_period ON ledger.journal_entry;
                DROP TRIGGER books_close_moves_forward ON ledger.books;
                DROP FUNCTION ledger.assert_entry_in_open_period();
                DROP FUNCTION ledger.assert_close_moves_forward();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_journal_entry_books_tenant_id_company_id",
                schema: "ledger",
                table: "journal_entry");

            migrationBuilder.DropTable(
                name: "books",
                schema: "ledger");
        }
    }
}
