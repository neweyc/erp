using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppPlatform.Ledger.Migrations
{
    /// <summary>
    /// The ledger's invariants, enforced by the database so they hold against ANY client of the
    /// ledger's runtime role — a handler bug, raw SQL, or code that is not in this repo. The handlers
    /// check first so callers get problem codes; these are the backstop.
    ///
    /// Four rules:
    ///
    /// 1. SEALED — an entry declares its line count when posted (fixed forever: entries are
    ///    append-only). A line's number must fall in 1..line_count, and numbers are unique per entry,
    ///    so once the entry is complete no line can ever be added to it.
    /// 2. COMPLETE AND BALANCED — at commit, an entry has exactly line_count lines, summing to zero.
    /// 3. NUMBERED — entry numbers in each (tenant, company, year) series are exactly 1..N: the counter
    ///    only ever advances by one, every number it issues ends up on an entry, and no entry carries
    ///    a number the counter has not issued.
    /// 4. REVERSED EXACTLY — a reversal mirrors its original line for line, in the same currency, dated
    ///    no earlier, and is not itself the reversal of something.
    ///
    /// Most checks are DEFERRED to COMMIT, because an entry, its lines and its counter row are separate
    /// INSERTs. A deferred check can be forced to run early (SET CONSTRAINTS … IMMEDIATE), so each must
    /// be queued by every row that could still break it afterwards — otherwise the next INSERT escapes
    /// it. Where one is queued only once, the comment says why nothing later can change what it saw.
    ///
    /// The functions run with the caller's privileges (not SECURITY DEFINER) and a pinned search_path,
    /// and only the table owner or a superuser — neither of which the runtime role is — can disable them.
    /// </summary>
    public partial class AddLedgerIntegrityTriggers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- 1. SEALED --------------------------------------------------------------------------

                -- Immediate, per line. Together with the unique (tenant, entry, line_number) index and
                -- rule 2's exact count, it leaves no free slot once an entry is complete.
                CREATE FUNCTION ledger.assert_line_within_entry() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                DECLARE
                  declared integer;
                BEGIN
                  SELECT line_count INTO declared FROM ledger.journal_entry
                   WHERE tenant_id = NEW.tenant_id AND id = NEW.entry_id;

                  -- The entry must already exist. NOT left to the foreign key: one statement can insert
                  -- lines BEFORE their entry (a data-modifying CTE), and the key is only checked once the
                  -- statement ends — by which time the entry exists and the range was never checked.
                  -- The application always writes the entry first, so this refuses nothing legitimate.
                  IF declared IS NULL THEN
                    RAISE EXCEPTION 'a line cannot be written before its journal entry %', NEW.entry_id
                      USING ERRCODE = 'check_violation';
                  END IF;

                  IF NEW.line_number < 1 OR NEW.line_number > declared THEN
                    RAISE EXCEPTION 'journal entry % has % lines: line % cannot be added to it',
                      NEW.entry_id, declared, NEW.line_number
                      USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NEW;
                END
                $$;

                CREATE TRIGGER journal_line_within_entry
                  BEFORE INSERT ON ledger.journal_line
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_line_within_entry();

                -- 2. COMPLETE AND BALANCED ------------------------------------------------------------

                -- Queued once, by the entry — and that is enough. It passes only when every declared
                -- line exists, and from then on rule 1 leaves no slot for another: forcing it to run
                -- early can make it fail sooner, never let a later line through. Queued by the entry
                -- rather than by its lines because an entry inserted with NO lines fires no line trigger.
                CREATE FUNCTION ledger.assert_entry_complete_and_balanced() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                DECLARE
                  written bigint;
                  total numeric;
                BEGIN
                  -- numeric, not bigint: a sum of large amounts must neither overflow into an error
                  -- that hides which rule failed nor wrap into a false balance.
                  SELECT count(*), coalesce(sum(amount_minor::numeric), 0)
                    INTO written, total
                    FROM ledger.journal_line
                   WHERE tenant_id = NEW.tenant_id AND entry_id = NEW.id;

                  IF written <> NEW.line_count THEN
                    RAISE EXCEPTION 'journal entry % declares % lines but has %', NEW.id, NEW.line_count, written
                      USING ERRCODE = 'check_violation';
                  END IF;

                  IF total <> 0 THEN
                    RAISE EXCEPTION 'journal entry % is unbalanced: its lines sum to %', NEW.id, total
                      USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER journal_entry_complete_and_balanced
                  AFTER INSERT ON ledger.journal_entry
                  DEFERRABLE INITIALLY DEFERRED
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_complete_and_balanced();

                -- 3. NUMBERED ------------------------------------------------------------------------

                -- The counter starts at 1 and moves one step at a time, within its own series.
                CREATE FUNCTION ledger.assert_sequence_advances_by_one() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                BEGIN
                  IF TG_OP = 'INSERT' AND NEW.last_number <> 1 THEN
                    RAISE EXCEPTION 'a number series must start at 1' USING ERRCODE = 'check_violation';
                  END IF;

                  IF TG_OP = 'UPDATE' AND (
                       NEW.last_number <> OLD.last_number + 1
                    OR (NEW.tenant_id, NEW.company_id, NEW.fiscal_year) <> (OLD.tenant_id, OLD.company_id, OLD.fiscal_year)) THEN
                    RAISE EXCEPTION 'a number series advances by exactly one' USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NEW;
                END
                $$;

                CREATE TRIGGER entry_sequence_advances_by_one
                  BEFORE INSERT OR UPDATE ON ledger.entry_sequence
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_sequence_advances_by_one();

                -- Every number the counter issues ends up on a committed entry — so a failed post must
                -- roll its number back rather than leave a gap. Queued by each advance, so each number
                -- is checked; an entry, once it exists, cannot be removed (append-only).
                CREATE FUNCTION ledger.assert_sequence_number_used() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                BEGIN
                  IF NOT EXISTS (
                    SELECT 1 FROM ledger.journal_entry
                     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
                       AND fiscal_year = NEW.fiscal_year AND number = NEW.last_number
                  ) THEN
                    RAISE EXCEPTION 'entry number % in % was issued but not used', NEW.last_number, NEW.fiscal_year
                      USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER entry_sequence_number_used
                  AFTER INSERT OR UPDATE ON ledger.entry_sequence
                  DEFERRABLE INITIALLY DEFERRED
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_sequence_number_used();

                -- No entry carries a number the counter has not reached. The counter only rises, so a
                -- check that passes cannot later become false.
                CREATE FUNCTION ledger.assert_entry_number_issued() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                BEGIN
                  IF NOT EXISTS (
                    SELECT 1 FROM ledger.entry_sequence
                     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
                       AND fiscal_year = NEW.fiscal_year AND last_number >= NEW.number
                  ) THEN
                    RAISE EXCEPTION 'entry number % in % was never issued', NEW.number, NEW.fiscal_year
                      USING ERRCODE = 'check_violation';
                  END IF;

                  RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER journal_entry_numbered
                  AFTER INSERT ON ledger.journal_entry
                  DEFERRABLE INITIALLY DEFERRED
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_number_issued();

                -- 4. REVERSED EXACTLY ----------------------------------------------------------------

                -- The check itself, shared by the two triggers below.
                CREATE FUNCTION ledger.check_reversal(tenant integer, reversal uuid) RETURNS void
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                DECLARE
                  this ledger.journal_entry;
                  original ledger.journal_entry;
                BEGIN
                  SELECT * INTO this FROM ledger.journal_entry WHERE tenant_id = tenant AND id = reversal;

                  -- Same company is already guaranteed by the (tenant, company, id) foreign key.
                  SELECT * INTO original FROM ledger.journal_entry
                   WHERE tenant_id = tenant AND id = this.reverses_entry_id;

                  IF original.reverses_entry_id IS NOT NULL
                     OR original.currency <> this.currency
                     OR this.entry_date < original.entry_date THEN
                    RAISE EXCEPTION 'entry % is not a valid reversal of %', this.id, original.id
                      USING ERRCODE = 'check_violation';
                  END IF;

                  -- Line for line: every original line negated, and nothing else. EXCEPT ALL in both
                  -- directions compares them as multisets, so a repeated line must be repeated too.
                  IF EXISTS (
                    (SELECT account_id, -amount_minor FROM ledger.journal_line
                      WHERE tenant_id = tenant AND entry_id = original.id
                     EXCEPT ALL
                     SELECT account_id, amount_minor FROM ledger.journal_line
                      WHERE tenant_id = tenant AND entry_id = this.id)
                    UNION ALL
                    (SELECT account_id, amount_minor FROM ledger.journal_line
                      WHERE tenant_id = tenant AND entry_id = this.id
                     EXCEPT ALL
                     SELECT account_id, -amount_minor FROM ledger.journal_line
                      WHERE tenant_id = tenant AND entry_id = original.id)
                  ) THEN
                    RAISE EXCEPTION 'entry % does not exactly negate entry %', this.id, original.id
                      USING ERRCODE = 'check_violation';
                  END IF;
                END
                $$;

                -- Queued when a reversal is inserted, and again by every line inserted into a reversal
                -- OR into an entry that has one — either side changing could break the mirror.
                CREATE FUNCTION ledger.assert_reversal_mirrors_original() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $$
                DECLARE
                  reversal uuid;
                BEGIN
                  IF TG_TABLE_NAME = 'journal_entry' THEN
                    IF NEW.reverses_entry_id IS NOT NULL THEN
                      PERFORM ledger.check_reversal(NEW.tenant_id, NEW.id);
                    END IF;
                    RETURN NULL;
                  END IF;

                  FOR reversal IN
                    SELECT id FROM ledger.journal_entry
                     WHERE tenant_id = NEW.tenant_id
                       AND reverses_entry_id IS NOT NULL
                       AND (id = NEW.entry_id OR reverses_entry_id = NEW.entry_id)
                  LOOP
                    PERFORM ledger.check_reversal(NEW.tenant_id, reversal);
                  END LOOP;

                  RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER journal_entry_reversal_mirrors_original
                  AFTER INSERT ON ledger.journal_entry
                  DEFERRABLE INITIALLY DEFERRED
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_reversal_mirrors_original();

                CREATE CONSTRAINT TRIGGER journal_line_reversal_mirrors_original
                  AFTER INSERT ON ledger.journal_line
                  DEFERRABLE INITIALLY DEFERRED
                  FOR EACH ROW EXECUTE FUNCTION ledger.assert_reversal_mirrors_original();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER journal_line_reversal_mirrors_original ON ledger.journal_line;
                DROP TRIGGER journal_entry_reversal_mirrors_original ON ledger.journal_entry;
                DROP TRIGGER journal_entry_numbered ON ledger.journal_entry;
                DROP TRIGGER entry_sequence_number_used ON ledger.entry_sequence;
                DROP TRIGGER entry_sequence_advances_by_one ON ledger.entry_sequence;
                DROP TRIGGER journal_entry_complete_and_balanced ON ledger.journal_entry;
                DROP TRIGGER journal_line_within_entry ON ledger.journal_line;
                DROP FUNCTION ledger.assert_reversal_mirrors_original();
                DROP FUNCTION ledger.check_reversal(integer, uuid);
                DROP FUNCTION ledger.assert_entry_number_issued();
                DROP FUNCTION ledger.assert_sequence_number_used();
                DROP FUNCTION ledger.assert_sequence_advances_by_one();
                DROP FUNCTION ledger.assert_entry_complete_and_balanced();
                DROP FUNCTION ledger.assert_line_within_entry();
                """);
        }
    }
}
