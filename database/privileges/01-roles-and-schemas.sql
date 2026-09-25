-- App Platform — roles, schemas, and default privileges
-- Run ONCE against a new database, as a superuser, BEFORE any migration.
--
-- Ordering is not a style choice. Default privileges apply only to objects created
-- AFTER they are set, so this must precede the first migration or every table it
-- creates will be missing its grants. 02-grants.sql repairs an existing database;
-- this file is what makes the repair unnecessary.
--
-- No passwords appear here and none ever should. Roles are created able to log in but
-- with no password set, which means they cannot authenticate at all until one is
-- issued out of band. A checked-in file with a password in it is a credential leak
-- with a git history.

\set ON_ERROR_STOP on

-- ---------------------------------------------------------------------------
-- Roles
-- ---------------------------------------------------------------------------

-- Owns every object. No process ever connects as this role: it exists so that
-- published views and SECURITY DEFINER functions have an owner whose privileges
-- they execute with, and which no runtime role can impersonate.
CREATE ROLE ap_owner NOLOGIN;

-- Runtime roles. One per deployable. None has DDL; an application bug cannot drop a
-- table and a compromised process cannot rewrite the schema.
CREATE ROLE ap_platform_rt LOGIN;
CREATE ROLE ap_core_rt     LOGIN;
CREATE ROLE ap_tickets_rt  LOGIN;

-- Migration roles. DDL within one schema only.
CREATE ROLE ap_platform_migrate LOGIN;
CREATE ROLE ap_core_migrate     LOGIN;
CREATE ROLE ap_tickets_migrate  LOGIN;

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
-- access in any role that can log in.
GRANT ap_platform_migrate, ap_core_migrate, ap_tickets_migrate TO ap_owner;

-- ---------------------------------------------------------------------------
-- Schemas
-- ---------------------------------------------------------------------------

CREATE SCHEMA platform    AUTHORIZATION ap_owner;
CREATE SCHEMA core        AUTHORIZATION ap_owner;
CREATE SCHEMA identity    AUTHORIZATION ap_owner;
CREATE SCHEMA tickets     AUTHORIZATION ap_owner;

-- Published contracts. Owned by ap_owner because a view executes with its OWNER's
-- privileges — that is the entire mechanism by which ap_tickets_rt reads
-- core_v1.employee while holding no grant whatsoever on core.employee.
CREATE SCHEMA core_v1     AUTHORIZATION ap_owner;
CREATE SCHEMA identity_v1 AUTHORIZATION ap_owner;

-- PostgreSQL grants CREATE on `public` to PUBLIC in older versions and USAGE in all of
-- them. A table that lands there sits outside every grant below, so the boundary
-- silently does not apply to it.
REVOKE ALL ON SCHEMA public FROM PUBLIC;

-- ap_owner may create schemas.
--
-- Needed because the published contracts live in their own schemas and their migrations run AS
-- ap_owner — `CREATE SCHEMA IF NOT EXISTS core_v1` checks database-level CREATE before noticing
-- the schema already exists, so without this the migration fails with "permission denied for
-- database". ap_owner is NOLOGIN and no process connects as it, so this widens nothing reachable.
-- Via current_database() because GRANT needs a literal identifier, and this script is applied
-- both by psql and by the test harness through Npgsql, which does not expand psql variables.
DO $$
BEGIN
  EXECUTE format('GRANT CREATE ON DATABASE %I TO ap_owner', current_database());
END
$$;

-- Migration roles may create within their own schema and nowhere else.
GRANT USAGE, CREATE ON SCHEMA platform TO ap_platform_migrate;
GRANT USAGE, CREATE ON SCHEMA core     TO ap_core_migrate;
GRANT USAGE, CREATE ON SCHEMA identity TO ap_core_migrate;
GRANT USAGE, CREATE ON SCHEMA tickets  TO ap_tickets_migrate;

-- Runtime roles may see into their schemas but never create in them.
GRANT USAGE ON SCHEMA platform    TO ap_platform_rt;
GRANT USAGE ON SCHEMA core        TO ap_core_rt;
GRANT USAGE ON SCHEMA identity    TO ap_core_rt;
GRANT USAGE ON SCHEMA tickets     TO ap_tickets_rt;
GRANT USAGE ON SCHEMA core_v1     TO ap_core_rt, ap_tickets_rt;
GRANT USAGE ON SCHEMA identity_v1 TO ap_core_rt, ap_tickets_rt;

-- The provisioning exception needs TWO grants, and the table-level one in 02-grants.sql
-- is inert without this. A GRANT on platform.tenant does nothing while the role cannot
-- enter the schema holding it — the failure is "permission denied for schema platform",
-- which reads like a missing table grant and sends you looking in the wrong file.
--
-- USAGE on a schema conveys no access to anything in it; each table still needs its own
-- grant. So core reaches platform.tenant and nothing else: tenant_app, billing, operator
-- accounts, and the operator audit log all stay unreadable.
GRANT USAGE ON SCHEMA platform TO ap_core_rt;

-- Deliberately absent: ap_platform_rt has USAGE on `platform` and nothing else. It
-- cannot see core, identity, tickets, or the published views. Withholding the field
-- key protects encrypted columns only; names, emails, and ticket titles are plaintext
-- and this omission is the only thing that keeps them unreadable.

-- ---------------------------------------------------------------------------
-- Default privileges
-- ---------------------------------------------------------------------------
-- Keyed to the CREATING role, so each must name the migration role that will create
-- the objects. Omitting FOR ROLE silently scopes them to the role running this script,
-- which is the superuser, and then nothing a migration creates is granted at all.

ALTER DEFAULT PRIVILEGES FOR ROLE ap_platform_migrate IN SCHEMA platform
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ap_platform_rt;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_platform_migrate IN SCHEMA platform
  GRANT USAGE, SELECT ON SEQUENCES TO ap_platform_rt;

ALTER DEFAULT PRIVILEGES FOR ROLE ap_core_migrate IN SCHEMA core
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ap_core_rt;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_core_migrate IN SCHEMA core
  GRANT USAGE, SELECT ON SEQUENCES TO ap_core_rt;

ALTER DEFAULT PRIVILEGES FOR ROLE ap_core_migrate IN SCHEMA identity
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ap_core_rt;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_core_migrate IN SCHEMA identity
  GRANT USAGE, SELECT ON SEQUENCES TO ap_core_rt;

ALTER DEFAULT PRIVILEGES FOR ROLE ap_tickets_migrate IN SCHEMA tickets
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ap_tickets_rt;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_tickets_migrate IN SCHEMA tickets
  GRANT USAGE, SELECT ON SEQUENCES TO ap_tickets_rt;

-- Published contracts are created by ap_owner and are READ ONLY to consumers. SELECT
-- and nothing else: a published view is a contract, not a back door into the owning
-- schema.
ALTER DEFAULT PRIVILEGES FOR ROLE ap_owner IN SCHEMA core_v1
  GRANT SELECT ON TABLES TO ap_core_rt, ap_tickets_rt;
ALTER DEFAULT PRIVILEGES FOR ROLE ap_owner IN SCHEMA identity_v1
  GRANT SELECT ON TABLES TO ap_core_rt, ap_tickets_rt;

-- Functions are EXECUTABLE BY PUBLIC on creation. Revoking that default here means a
-- new SECURITY DEFINER function is not accidentally callable by every role in the
-- cluster between its migration and someone remembering to lock it down.
-- A per-schema REVOKE cannot subtract PostgreSQL's global PUBLIC default.
ALTER DEFAULT PRIVILEGES FOR ROLE ap_owner
  REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
