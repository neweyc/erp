-- Stands in for the real EF migrations, so the default privileges set in 01 are
-- actually exercised: each table is created BY the migration role that will create it.
\set ON_ERROR_STOP on

SET ROLE ap_platform_migrate;
CREATE TABLE platform.tenant (
  id serial PRIMARY KEY, public_id text NOT NULL, name text NOT NULL, status text NOT NULL,
  idle_timeout_minutes int);
CREATE TABLE platform.tenant_app (
  tenant_id int NOT NULL, app text NOT NULL, PRIMARY KEY (tenant_id, app));
CREATE TABLE platform.audit_log (id bigserial PRIMARY KEY, detail text);
RESET ROLE;

SET ROLE ap_core_migrate;
CREATE TABLE core.employee (
  id uuid PRIMARY KEY, tenant_id int NOT NULL, company_id int NOT NULL,
  public_id text NOT NULL, display_name text NOT NULL, phone text);
CREATE TABLE core.company (id serial PRIMARY KEY, tenant_id int NOT NULL, name text NOT NULL);
CREATE TABLE identity."user" (
  id uuid PRIMARY KEY, tenant_id int NOT NULL, company_id int NOT NULL DEFAULT 1,
  email text NOT NULL, role text NOT NULL,
  employee_id uuid, active boolean NOT NULL DEFAULT true);
CREATE TABLE identity.session (
  id uuid PRIMARY KEY, user_id uuid NOT NULL, last_seen_at timestamptz NOT NULL,
  revoked_at timestamptz, absolute_expiry timestamptz NOT NULL,
  mfa_satisfied boolean NOT NULL DEFAULT true);
RESET ROLE;

SET ROLE ap_tickets_migrate;
CREATE TABLE tickets.ticket (
  id uuid PRIMARY KEY, tenant_id int NOT NULL, public_id text NOT NULL, title text NOT NULL,
  assignee_employee_id uuid, assignee_display_name text);

-- One outbox per owning schema. A shared table would make every save a cross-schema write,
-- breaking the boundary with the mechanism meant to respect it.
CREATE TABLE tickets.outbox_event (
  id uuid PRIMARY KEY, tenant_id int NOT NULL, aggregate_type varchar(50) NOT NULL,
  aggregate_public_id varchar(40) NOT NULL, aggregate_version bigint NOT NULL,
  event_type varchar(100) NOT NULL, payload jsonb NOT NULL, occurred_at timestamptz NOT NULL,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, aggregate_type, aggregate_public_id, aggregate_version));

CREATE TABLE tickets.outbox_message (
  id uuid PRIMARY KEY, tenant_id int NOT NULL, event_id uuid,
  transport varchar(20) NOT NULL, destination varchar(320) NOT NULL, payload jsonb NOT NULL,
  status varchar(20) NOT NULL, attempts int NOT NULL, next_attempt_at timestamptz NOT NULL,
  locked_until timestamptz, completed_at timestamptz, last_error varchar(1000),
  created_at timestamptz NOT NULL,
  FOREIGN KEY (tenant_id, event_id) REFERENCES tickets.outbox_event (tenant_id, id) ON DELETE CASCADE);

CREATE INDEX outbox_message_due ON tickets.outbox_message (status, next_attempt_at)
  WHERE status = 'Pending';
RESET ROLE;

-- Published contracts, created as ap_owner. A view executes with its OWNER's privileges,
-- which is what lets a consumer read it while holding nothing on the tables behind it.
SET ROLE ap_owner;

CREATE VIEW core_v1.employee AS
  SELECT id, tenant_id, company_id, public_id, display_name FROM core.employee;

CREATE VIEW core_v1.company AS
  SELECT id, tenant_id, name FROM core.company;

-- A keyed lookup, deliberately NOT a view: a grant on a view is a grant to read all of
-- it, and a session id is credential-equivalent.
CREATE FUNCTION identity_v1.session_context(p_session_id uuid)
RETURNS TABLE (
  session_id uuid, user_id uuid, tenant_id int, company_id int, tenant_status text,
  role text, user_active boolean, mfa_satisfied boolean, last_seen_at timestamptz,
  absolute_expiry timestamptz, revoked_at timestamptz, employee_id uuid,
  licensed_apps text[], idle_timeout_minutes int)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT s.id, u.id, u.tenant_id, u.company_id, t.status,
         u.role, u.active, s.mfa_satisfied, s.last_seen_at,
         s.absolute_expiry, s.revoked_at, u.employee_id,
         coalesce(array_agg(ta.app) FILTER (WHERE ta.app IS NOT NULL), '{}'),
         t.idle_timeout_minutes
  FROM identity.session s
  JOIN identity."user" u ON u.id = s.user_id
  JOIN platform.tenant t ON t.id = u.tenant_id
  LEFT JOIN platform.tenant_app ta ON ta.tenant_id = u.tenant_id
  WHERE s.id = p_session_id
  GROUP BY s.id, u.id, u.tenant_id, u.company_id, t.status, u.role, u.active,
           s.mfa_satisfied, s.last_seen_at, s.absolute_expiry, s.revoked_at,
           u.employee_id, t.idle_timeout_minutes;
$$;

CREATE FUNCTION identity_v1.touch_session(p_session_id uuid)
RETURNS void
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
  UPDATE identity.session SET last_seen_at = now() WHERE id = p_session_id;
$$;

RESET ROLE;
