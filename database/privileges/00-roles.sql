-- App Platform — roles
-- Run as a superuser BEFORE 01-schemas.sql, and before restoring a backup.
--
-- Roles are CLUSTER-wide; everything else the grant model needs lives inside one database. They
-- are separate from 01 for exactly that reason: a restore needs the roles to exist first, because
-- the dump assigns every object to its owner and grants to the runtime roles by name. A dump does
-- NOT contain roles, and running 01 before a restore would also create the schemas, which the dump
-- then fails to create.
--
-- Idempotent, unlike 01: a role that already exists is left as it is. That is what lets a backup
-- be restored into a second database on a cluster that already runs this platform, the usual way
-- to rehearse a restore. The cost is that an existing role keeps whatever attributes it already
-- has; 99-verify.sql reports the dangerous one (an application role that is a superuser).
--
-- No passwords appear here and none ever should. Login roles are created with no password set,
-- so they cannot authenticate until one is issued out of band. A checked-in file with a password
-- in it is a credential leak with a git history.

\set ON_ERROR_STOP on

DO $$
DECLARE
  role_name text;
BEGIN
  -- Owns every object. No process ever connects as this role: it exists so that
  -- published views and SECURITY DEFINER functions have an owner whose privileges
  -- they execute with, and which no runtime role can impersonate.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ap_owner') THEN
    CREATE ROLE ap_owner NOLOGIN;
  END IF;

  -- Runtime roles (_rt), one per deployable, with no DDL: an application bug cannot drop a table
  -- and a compromised process cannot rewrite the schema. Migration roles (_migrate) have DDL
  -- within one schema only; 01 grants them that.
  FOREACH role_name IN ARRAY ARRAY[
    'ap_platform_rt', 'ap_core_rt', 'ap_tickets_rt', 'ap_ledger_rt',
    'ap_platform_migrate', 'ap_core_migrate', 'ap_tickets_migrate', 'ap_ledger_migrate'
  ]
  LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
      EXECUTE format('CREATE ROLE %I LOGIN', role_name);
    END IF;
  END LOOP;
END
$$;

-- ap_owner inherits from every migration role, and this is load-bearing rather than
-- tidiness.
--
-- A view executes with its OWNER's privileges — but a table is owned by whoever CREATED
-- it, which is the migration role, not ap_owner. Without this, core_v1.employee is owned
-- by ap_owner, granted to ap_tickets_rt, and still fails: the consumer's grant on the
-- view is fine, and the view's owner cannot read core.employee. The error names the
-- underlying table, so it reads as a missing grant on a table the consumer is not
-- supposed to have one on, and sends you looking in the wrong place entirely.
--
-- Membership rather than per-table grants because it covers every table a migration
-- creates later, automatically. Nothing connects as ap_owner, so this concentrates no
-- access in any role that can log in. Re-granting an existing membership is a no-op.
GRANT ap_platform_migrate, ap_core_migrate, ap_tickets_migrate, ap_ledger_migrate TO ap_owner;
