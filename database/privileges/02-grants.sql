-- App Platform — grants over EXISTING objects
-- Run AFTER migrations, as a superuser. Idempotent: safe to re-run at any time.
--
-- 01-roles-and-schemas.sql sets default privileges so that objects created later are
-- granted automatically. This file exists for the cases where that is not enough:
-- a database migrated before the defaults were set, an object created by the wrong
-- role, or the narrow exceptions below that no default privilege can express.

\set ON_ERROR_STOP on

-- ONE transaction, committed at the end of the file. The bulk re-grant below hands UPDATE and
-- DELETE on every table — audit logs included — to the runtime roles, and the audit section
-- takes them back. Run statement by statement, a live runtime connection could rewrite history
-- in between, and a script stopped part-way would leave the logs writable. Grants are
-- transactional in PostgreSQL, so other sessions see only the final state or none of it.
BEGIN;

-- ---------------------------------------------------------------------------
-- Bulk re-grant (repairs drift; no-ops on a correctly bootstrapped database)
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA platform TO ap_platform_rt;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA platform TO ap_platform_rt;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA core     TO ap_core_rt;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA core     TO ap_core_rt;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA identity TO ap_core_rt;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA identity TO ap_core_rt;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA tickets  TO ap_tickets_rt;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA tickets  TO ap_tickets_rt;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA ledger   TO ap_ledger_rt;
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA ledger   TO ap_ledger_rt;

GRANT SELECT ON ALL TABLES IN SCHEMA core_v1     TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;
GRANT SELECT ON ALL TABLES IN SCHEMA identity_v1 TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;

-- Repairs a database bootstrapped before ap_owner was made a member of the migration
-- roles. Without it every published view fails on the table behind it.
GRANT ap_platform_migrate, ap_core_migrate, ap_tickets_migrate, ap_ledger_migrate TO ap_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- Append-only tables
-- ---------------------------------------------------------------------------
-- Every `audit_log`, in every schema, plus the tables listed below, lose UPDATE, DELETE and
-- TRUNCATE for every runtime role. A posted journal is corrected by a reversal, never an edit;
-- an audit row is never corrected at all. The code refuses both too (IAppendOnly), but only for
-- code that goes through it — this is the defence that holds against code that is not in this
-- repo.
--
-- The default privileges in 01 grant all of these to any new table, and the bulk re-grant above
-- restores them — so this must come AFTER both.
--
-- Audit logs are found by NAME, as are the runtime roles (`ap_%_rt`), so a new app's log and role
-- are covered with no edit here once this file runs after that app's first migration, as the
-- runbook in database/README.md requires. Other append-only tables are LISTED: BoundaryTests
-- fails if a table mapped by an IAppendOnly entity is missing from this list, or from the same
-- list in 99-verify.sql. A table that does not exist yet is skipped, so this runs before a
-- service's first migration as well as after it.
--
-- Column-level UPDATE is revoked separately. A table-level REVOKE does not remove a grant made
-- on individual columns, so `GRANT UPDATE (changes)` would otherwise survive this script.

DO $$
DECLARE
  append_only regclass;
  role        name;
  columns     text;
BEGIN
  FOR append_only IN
    SELECT c.oid::regclass
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relname = 'audit_log' AND c.relkind = 'r'
      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
    UNION
    SELECT to_regclass(listed)
    FROM unnest(ARRAY[
      'ledger.journal_entry',
      'ledger.journal_line'
    ]) AS listed
    WHERE to_regclass(listed) IS NOT NULL
  LOOP
    SELECT string_agg(quote_ident(a.attname), ', ') INTO columns
    FROM pg_attribute a
    WHERE a.attrelid = append_only AND a.attnum > 0 AND NOT a.attisdropped;

    FOR role IN SELECT rolname FROM pg_roles WHERE rolname LIKE 'ap\_%\_rt' LOOP
      EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM %I', append_only, role);
      EXECUTE format('REVOKE UPDATE (%s) ON %s FROM %I', columns, append_only, role);
    END LOOP;
  END LOOP;
END
$$;

-- ---------------------------------------------------------------------------
-- The ledger's number counter, and its integrity checks
-- ---------------------------------------------------------------------------
-- The runtime role advances it (UPDATE) but may never remove it. Deleting or truncating a
-- series would restart it at 1 and reissue numbers already on posted entries. The unique index
-- would then refuse those posts, so the books would not be corrupted — but the series would be
-- stuck, and restarting a legally numbered series is not something an application gets to do.

--
-- The reversal check is a function its triggers call, and a trigger runs as the role that fired it
-- — so the ledger's role needs EXECUTE on it. Functions created by ap_owner are not executable by
-- PUBLIC (01-roles-and-schemas.sql), which is why it is granted here by name. It only reads.

DO $$
BEGIN
  IF to_regclass('ledger.entry_sequence') IS NOT NULL THEN
    REVOKE DELETE, TRUNCATE ON ledger.entry_sequence FROM ap_ledger_rt;
  END IF;

  IF to_regprocedure('ledger.check_reversal(integer, uuid)') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION ledger.check_reversal(integer, uuid) TO ap_ledger_rt;
  END IF;
END
$$;

-- ---------------------------------------------------------------------------
-- The provisioning exception
-- ---------------------------------------------------------------------------
-- The one deliberate hole in the boundary. The tenant row, its company, the starter
-- seeds, and the admin invite must commit in a single transaction, and a transaction
-- cannot span two services — so core writes the tenant row.
--
-- Split by OPERATION, and the split carries meaning worth preserving: core can bring a
-- tenant into existence; only the operator can change what it is permitted to do.

-- USAGE on the schema is granted in 01. Repeated here because this file must also repair
-- a database bootstrapped before that line existed, and the table grant alone is inert.
GRANT USAGE ON SCHEMA platform TO ap_core_rt;
GRANT SELECT, INSERT ON platform.tenant TO ap_core_rt;
REVOKE UPDATE, DELETE ON platform.tenant FROM ap_core_rt;

-- Sequence access for the tenant key, if it is an identity/serial column. Harmless
-- when it is not.
DO $$
BEGIN
  IF pg_get_serial_sequence('platform.tenant', 'id') IS NOT NULL THEN
    EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO ap_core_rt',
                   pg_get_serial_sequence('platform.tenant', 'id'));
  END IF;
END
$$;

-- No other platform table is reachable from core: no entitlement row, billing record,
-- operator account, or audit entry.

-- ---------------------------------------------------------------------------
-- Session lookup and touch
-- ---------------------------------------------------------------------------
-- Functions, not views. A grant on a view is a grant to read ALL of it, and
-- `SELECT * FROM identity_v1.session_context` would enumerate every live session in
-- every tenant — with a session id being credential-equivalent. Views are for joining
-- and paging; a keyed lookup that must not enumerate is a function.
--
-- Granted to CUSTOMER roles only. ap_platform_rt is deliberately absent: operator
-- sessions live in platform tables, and letting the platform resolve tenant sessions
-- would undo the isolation everything else here pays for.

REVOKE EXECUTE ON FUNCTION identity_v1.session_context(uuid) FROM PUBLIC;
GRANT  EXECUTE ON FUNCTION identity_v1.session_context(uuid) TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;

REVOKE EXECUTE ON FUNCTION identity_v1.touch_session(uuid) FROM PUBLIC;
GRANT  EXECUTE ON FUNCTION identity_v1.touch_session(uuid) TO ap_core_rt, ap_tickets_rt, ap_ledger_rt;

-- ---------------------------------------------------------------------------
-- Explicit negatives
-- ---------------------------------------------------------------------------
-- Stated rather than assumed. A REVOKE of a privilege that was never granted is a
-- no-op; writing them down means a future GRANT added elsewhere is contradicted here
-- rather than quietly taking effect.

REVOKE ALL ON SCHEMA core, identity, tickets, ledger, core_v1, identity_v1 FROM ap_platform_rt;
REVOKE ALL ON SCHEMA platform FROM ap_tickets_rt, ap_ledger_rt;
REVOKE ALL ON SCHEMA core, identity FROM ap_tickets_rt, ap_ledger_rt;
-- Apps never reach each other. They share nothing but core's published views; app-to-app
-- workflow, when it exists, goes through events.
REVOKE ALL ON SCHEMA ledger FROM ap_tickets_rt;
REVOKE ALL ON SCHEMA tickets FROM ap_ledger_rt;

COMMIT;
