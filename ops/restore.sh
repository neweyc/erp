#!/usr/bin/env bash
# Restores a backup made by ops/backup.sh into an EMPTY database, and proves the result.
#
#   1. Refuses unless the target database is empty. This never restores over existing data.
#   2. Decrypts the backup (needs the age identity: the PRIVATE key).
#   3. Creates the roles (database/privileges/00-roles.sql). A dump does not contain them.
#   4. Restores the data AND revokes every session (ops/after-restore.sql, which says why) in ONE
#      transaction, keeping every owner and grant. Either all of it commits, or none of it does:
#      there is never a committed copy whose restored sessions still work.
#   5. Compares every table's row count with the manifest taken at backup time.
#   6. Runs database/privileges/99-verify.sql and fails on any finding.
#
# A database that passes all six is a copy of the backup with its security boundary intact. It
# is not yet ready for traffic; the steps after this script are in ops/README.md. The first of
# them is to start core.api with Outbox__DeliveryPaused=true, because the restored outbox
# includes messages already sent after the backup was taken.
#
# Usage:
#   PGHOST=... PGPORT=... PGUSER=... PGDATABASE=<empty target> \
#     ops/restore.sh --identity /path/to/identity.txt /path/to/appplatform-<timestamp>.tar.age
#
# Connects as a superuser, or the provider's admin role: it creates roles and assigns ownership
# to them. Needs psql, pg_restore (no older than the dump's pg_dump) and age.

set -euo pipefail

# The decrypted dump is plaintext customer data. Readable by its owner only, and deleted on exit.
umask 077

here="$(cd "$(dirname "$0")" && pwd)"
privileges="$here/../database/privileges"

die() {
  echo "restore: $*" >&2
  exit 1
}

identity=""
backup=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --identity) identity="${2:-}"; shift 2 ;;
    -*)         die "unknown option '$1'. Usage: restore.sh --identity <age identity file> <backup.tar.age>" ;;
    *)          backup="$1"; shift ;;
  esac
done

[[ -n "$identity" && -f "$identity" ]] || die "--identity must name the age identity (private key) file."
[[ -n "$backup" && -f "$backup" ]]     || die "name the backup file to restore (appplatform-<timestamp>.tar.age)."
: "${PGDATABASE:?Set PGDATABASE to the EMPTY database to restore into.}"

# psql for this script: no ~/.psqlrc, quiet, unaligned, stop at the first error.
run_psql() {
  psql -X -q -t -A -v ON_ERROR_STOP=1 "$@"
}

# ---------------------------------------------------------------------------------------------
# 1. The target must be empty.
#
# The one mistake this script must make impossible is restoring over a live database. A database
# fresh from `createdb` has no schemas beyond `public`, nothing in `public`, no extension but
# plpgsql, and no large objects. Anything else means PGDATABASE points somewhere it should not,
# and the script stops before changing anything. Each kind is counted separately so the refusal
# can say what it found.
# ---------------------------------------------------------------------------------------------
echo "==> checking that $PGDATABASE is empty"
found="$(run_psql -F ': ' -c "
  SELECT kind, n FROM (
    SELECT 'schemas' AS kind, count(*) AS n FROM pg_namespace
      WHERE nspname NOT IN ('public', 'information_schema') AND nspname NOT LIKE 'pg\_%'
    UNION ALL
    SELECT 'tables, views, sequences or indexes in public', count(*)
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'
    UNION ALL
    SELECT 'functions in public', count(*)
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public'
    UNION ALL
    SELECT 'types in public', count(*)
      FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'public'
    UNION ALL
    SELECT 'extensions', count(*) FROM pg_extension WHERE extname <> 'plpgsql'
    UNION ALL
    SELECT 'large objects', count(*) FROM pg_largeobject_metadata
  ) AS existing
  WHERE n > 0")"

[[ -z "$found" ]] \
  || die "$PGDATABASE is not empty (found $(echo "$found" | paste -sd, -)). Restore into a NEW database (createdb <name>); this script never restores over existing data."

# Set once the target starts changing (step 3), and once every check has passed (the end). If the
# script stops in between, the database may hold a partial or unverified copy, and whoever ran it
# must be told plainly not to use it.
target_changed=false
restore_finished=false
work_dir="$(mktemp -d)"

on_exit() {
  rm -rf "$work_dir"
  if [[ "$target_changed" == true && "$restore_finished" != true ]]; then
    echo "restore: DID NOT FINISH. Do not use $PGDATABASE: drop it (dropdb $PGDATABASE) and restore into a new database." >&2
  fi
}
trap on_exit EXIT

# ---------------------------------------------------------------------------------------------
# 2. Decrypt.
# ---------------------------------------------------------------------------------------------

echo "==> decrypting $(basename "$backup")"
# age authenticates what it decrypts, so a corrupted or altered backup fails here, not halfway
# through the restore.
age --decrypt --identity "$identity" "$backup" | tar -C "$work_dir" -xf -
for part in about.txt database.dump manifest.tsv; do
  [[ -f "$work_dir/$part" ]] || die "the backup has no $part; it was not made by ops/backup.sh."
done
cat "$work_dir/about.txt"

# Refused before the target is touched. pg_restore writes large objects inside a BEGIN ... COMMIT
# of its own, and that COMMIT would end step 4's transaction early, committing restored data before
# the sessions are revoked. The application stores no large objects and backup.sh refuses to back
# them up; this catches a backup made some other way.
# The listing goes to a file first: piped straight into `grep -q`, an early match kills pg_restore
# with SIGPIPE, pipefail reports the pipeline as failed, and the check would read a match as none.
pg_restore --list "$work_dir/database.dump" > "$work_dir/contents.txt"
# Anchored to the entry-type column ("<id>; <oid> <oid> BLOB ..."), so a table that happens to be
# NAMED blobs is not mistaken for one.
if grep -qE '^[0-9]+; [0-9]+ [0-9]+ BLOBS?( |$)' "$work_dir/contents.txt"; then
  die "the backup contains large objects, which this script cannot restore in one transaction."
fi

# ---------------------------------------------------------------------------------------------
# 3. Roles, and the one database-level grant.
# ---------------------------------------------------------------------------------------------
target_changed=true
echo "==> creating roles (00-roles.sql)"
# Notices silenced: on a cluster that already has the roles, each re-granted membership prints
# one, and a page of them in the middle of a restore reads like something going wrong.
PGOPTIONS="-c client_min_messages=warning" run_psql -f "$privileges/00-roles.sql"

# Database-level privileges are not in a dump unless it was taken with --create, and that would
# tie the restore to the original database name. This is the only one the model needs: the
# published-contract migrations run as ap_owner and create their schemas (01-schemas.sql says why).
run_psql -c "DO \$\$ BEGIN EXECUTE format('GRANT CREATE ON DATABASE %I TO ap_owner', current_database()); END \$\$;"

# ---------------------------------------------------------------------------------------------
# 4. Restore and revoke every session, in ONE transaction.
#
# Separately, a crash between the two would commit a copy whose restored sessions still work, and
# a retry would then refuse the no-longer-empty target. So pg_restore writes its SQL to stdout
# instead of into the database, and it goes to psql with after-restore.sql behind it, wrapped in
# one BEGIN ... COMMIT.
#
# COMMIT is sent ONLY if pg_restore itself succeeded (the && chain). If it fails part-way, psql
# reaches the end of its input inside an open transaction and the server rolls it back: nothing
# half-restored is ever committed. psql's ON_ERROR_STOP does the same for a failing statement.
#
# Owners and grants are kept (no --no-owner), for the reason given in backup.sh.
# ---------------------------------------------------------------------------------------------
echo "==> restoring, and revoking every session (after-restore.sql), in one transaction"
{
  echo "BEGIN;"
  pg_restore --file=- "$work_dir/database.dump" \
    && cat "$here/after-restore.sql" \
    && echo "COMMIT;"
} | run_psql

# ---------------------------------------------------------------------------------------------
# 5. Every table holds exactly what the backup held.
#
# Sorted with a byte-order sort on both sides, because the two servers' collations can order
# the same names differently and a reordering is not a difference.
# ---------------------------------------------------------------------------------------------
echo "==> comparing row counts with the manifest"
run_psql -F $'\t' -f "$here/table-counts.sql" | LC_ALL=C sort > "$work_dir/restored.tsv"
LC_ALL=C sort "$work_dir/manifest.tsv" > "$work_dir/expected.tsv"

if ! diff -u "$work_dir/expected.tsv" "$work_dir/restored.tsv"; then
  die "row counts differ from the manifest (lines marked - were in the backup, + are in the restore). Do not use this database."
fi
echo "    $(wc -l < "$work_dir/expected.tsv" | tr -d ' ') tables match"

# ---------------------------------------------------------------------------------------------
# 6. The security boundary survived the trip.
#
# 99-verify.sql lists violations, so zero rows is the pass. Most likely failure: a restore that
# lost an owner, which leaves a published view owned by the wrong role.
# ---------------------------------------------------------------------------------------------
echo "==> verifying privileges (99-verify.sql)"
findings="$(run_psql -f "$privileges/99-verify.sql")"

if [[ -n "$findings" ]]; then
  echo "$findings" >&2
  die "99-verify.sql reported the findings above. Do not use this database."
fi

restore_finished=true

cat <<EOF

==> restored and verified: $PGDATABASE

Every session is revoked and the data matches the backup. Before sending traffic here, follow
"After the script" in ops/README.md. In particular, start core.api with

    Outbox__DeliveryPaused=true

and review core.outbox_message before resuming delivery: it holds messages the live system had
already sent after this backup was taken.
EOF
