-- App Platform — boundary verification
-- Run as a superuser after 01 + 02 + migrations. ZERO ROWS means the boundary holds.
--
-- Every check here asserts a NEGATIVE — that some role CANNOT do something. Asserting
-- the positives instead would pass cleanly against a database where every role can read
-- everything, which is precisely the failure this file exists to catch.

\set ON_ERROR_STOP on

WITH
published_schemas AS (SELECT unnest(ARRAY['core_v1','identity_v1']) AS nspname),
owned_schemas     AS (SELECT unnest(ARRAY['platform','core','identity','tickets']) AS nspname),
runtime_roles     AS (SELECT unnest(ARRAY['ap_platform_rt','ap_core_rt','ap_tickets_rt']) AS rolname),

-- 1. security_invoker on a published view resolves permissions as the CALLER, which
--    collapses the entire boundary: the consumer has no grant on the underlying table,
--    so the view would simply stop working — or worse, work for a role that does.
invoker_views AS (
  SELECT 'published view has security_invoker' AS check_name,
         format('%s.%s', n.nspname, c.relname) AS detail
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN published_schemas p ON p.nspname = n.nspname
  WHERE c.relkind = 'v'
    AND array_to_string(c.reloptions, ',') ILIKE '%security_invoker=true%'
),

-- 2. A published schema holds views only. A table there is a writable back door into
--    data the consumer does not own.
tables_in_published AS (
  SELECT 'table in a published schema (expected a view)',
         format('%s.%s', n.nspname, c.relname)
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN published_schemas p ON p.nspname = n.nspname
  WHERE c.relkind = 'r'
),

-- 3. A published view owned by anything but ap_owner executes with the wrong
--    privileges, and a migration role's rights do not span the schemas some views read.
misowned_views AS (
  SELECT 'published view not owned by ap_owner',
         format('%s.%s owned by %s', n.nspname, c.relname, pg_get_userbyid(c.relowner))
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN published_schemas p ON p.nspname = n.nspname
  WHERE c.relkind = 'v' AND pg_get_userbyid(c.relowner) <> 'ap_owner'
),

-- 4. A SECURITY DEFINER function without a pinned search_path lets a caller who can
--    create objects shadow an unqualified name inside the body and have it run as the
--    owner. This is the classic privilege escalation, and it is silent.
unpinned_definers AS (
  SELECT 'SECURITY DEFINER function without pinned search_path',
         format('%s.%s', n.nspname, p.proname)
  FROM pg_proc p
  JOIN pg_namespace n ON n.oid = p.pronamespace
  WHERE p.prosecdef
    AND n.nspname NOT IN ('pg_catalog','information_schema')
    AND NOT EXISTS (
      SELECT 1 FROM unnest(coalesce(p.proconfig, ARRAY[]::text[])) AS cfg
      WHERE cfg LIKE 'search_path=%'
    )
),

-- 5. A superuser runtime role makes every other check here decorative.
superuser_roles AS (
  SELECT 'application role is a superuser', rolname
  FROM pg_roles WHERE rolname LIKE 'ap\_%' AND rolsuper
),

-- 6. No runtime role may create objects anywhere. DDL belongs to migration roles.
runtime_with_create AS (
  SELECT 'runtime role holds CREATE on a schema',
         format('%s on %s', r.rolname, s.nspname)
  FROM runtime_roles r
  CROSS JOIN (SELECT nspname FROM owned_schemas UNION SELECT nspname FROM published_schemas) s
  WHERE has_schema_privilege(r.rolname, s.nspname, 'CREATE')
),

-- 7. THE platform boundary. Anything reachable here is customer data the platform is
--    not permitted to see, encrypted columns notwithstanding.
platform_reach AS (
  SELECT 'ap_platform_rt can reach a customer schema', s.nspname
  FROM (SELECT unnest(ARRAY['core','identity','tickets','core_v1','identity_v1']) AS nspname) s
  WHERE has_schema_privilege('ap_platform_rt', s.nspname, 'USAGE')
),
platform_table_reach AS (
  SELECT 'ap_platform_rt can read a customer table',
         format('%s.%s', n.nspname, c.relname)
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname IN ('core','identity','tickets','core_v1','identity_v1')
    AND c.relkind IN ('r','v')
    AND has_table_privilege('ap_platform_rt', c.oid, 'SELECT')
),

-- 8. An app reads the published view, never the schema behind it.
app_bypasses_view AS (
  SELECT 'ap_tickets_rt can reach an owning schema directly', s.nspname
  FROM (SELECT unnest(ARRAY['core','identity','platform']) AS nspname) s
  WHERE has_schema_privilege('ap_tickets_rt', s.nspname, 'USAGE')
),

-- 9. Core may create a tenant; only the operator may change one. The split is the whole
--    point of the exception.
core_overreach AS (
  SELECT 'ap_core_rt can modify platform.tenant', priv
  FROM (SELECT unnest(ARRAY['UPDATE','DELETE']) AS priv) p
  WHERE to_regclass('platform.tenant') IS NOT NULL
    AND has_table_privilege('ap_core_rt', 'platform.tenant', p.priv)
),
core_platform_reach AS (
  SELECT 'ap_core_rt can read a platform table other than tenant',
         format('%s.%s', n.nspname, c.relname)
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname = 'platform' AND c.relkind IN ('r','v') AND c.relname <> 'tenant'
    AND has_table_privilege('ap_core_rt', c.oid, 'SELECT')
),

-- 10. Anything in `public` sits outside every grant above, so the boundary does not
--     apply to it at all.
public_objects AS (
  SELECT 'object in the public schema', c.relname
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname = 'public' AND c.relkind IN ('r','v')
),

-- 11. The session lookup must not be reachable by the platform.
session_fn_reach AS (
  SELECT 'ap_platform_rt can execute the session lookup', p.proname
  FROM pg_proc p
  JOIN pg_namespace n ON n.oid = p.pronamespace
  WHERE n.nspname IN ('identity','identity_v1')
    AND p.proname IN ('session_context','touch_session')
    AND has_function_privilege('ap_platform_rt', p.oid, 'EXECUTE')
)

SELECT * FROM invoker_views
UNION ALL SELECT * FROM tables_in_published
UNION ALL SELECT * FROM misowned_views
UNION ALL SELECT * FROM unpinned_definers
UNION ALL SELECT * FROM superuser_roles
UNION ALL SELECT * FROM runtime_with_create
UNION ALL SELECT * FROM platform_reach
UNION ALL SELECT * FROM platform_table_reach
UNION ALL SELECT * FROM app_bypasses_view
UNION ALL SELECT * FROM core_overreach
UNION ALL SELECT * FROM core_platform_reach
UNION ALL SELECT * FROM public_objects
UNION ALL SELECT * FROM session_fn_reach
ORDER BY 1, 2;
