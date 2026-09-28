-- The row count of every table in the database, one "schema.table<TAB>count" line each.
--
-- ops/backup.sh runs this inside the dump's own snapshot and stores the result as the backup's
-- manifest; ops/restore.sh runs it again on the restored database and requires the two to match.
-- That catches a restore that "succeeded" while missing data, which is the kind of failure that
-- is otherwise discovered by a customer.
--
-- Every table outside the system schemas, not a list of ours, so a table added by a later
-- migration is covered without an edit here. count(*) reads each table in full; at this
-- platform's size that is seconds. Revisit if a backup's manifest step becomes the slow part.
--
-- \gexec runs each generated statement in turn. Run with psql -t -A -F '<TAB>' for the format above.

SELECT format('SELECT %L, count(*) FROM %I.%I', n.nspname || '.' || c.relname, n.nspname, c.relname)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r'
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND n.nspname NOT LIKE 'pg\_%'
ORDER BY n.nspname, c.relname
\gexec
