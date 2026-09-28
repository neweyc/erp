#!/usr/bin/env bash
# Takes an encrypted, consistent backup of the whole app-platform database.
#
# Produces ONE file, <out-dir>/appplatform-<UTC timestamp>.tar.age, containing:
#   database.dump  pg_dump custom format: every schema, table, view, function, owner and grant.
#                  Roles are cluster-wide and NOT in it; restore creates them from 00-roles.sql.
#   manifest.tsv   the row count of every table, from the same snapshot as the dump, so a
#                  restore can prove it holds all of it (ops/restore.sh compares them).
#   about.txt      when, from where, and with which versions.
#
# Encrypted with age to a PUBLIC key (the recipient). The machine taking backups therefore cannot
# read them: only the holder of the matching identity (the private key) can, and that key never
# needs to be on this machine. The backup contains plaintext names, emails and ticket text, so an
# unencrypted copy must never leave this script.
#
# Usage:
#   PGHOST=... PGPORT=... PGUSER=... PGDATABASE=appplatform \
#     ops/backup.sh --recipient age1... --out /path/to/backups
#
# Connects with the standard libpq environment (PGHOST, PGUSER, PGPASSWORD or ~/.pgpass, ...).
# The role must be able to read every schema: a superuser, or the provider's admin role.
# Needs bash 4 or later (for coproc; macOS ships 3.2), psql and pg_dump no older than the server's
# major version, and age — ops/Dockerfile builds an image with exactly these.
#
# Exits non-zero on any failure, and a failed run leaves no file that looks like a backup.

set -euo pipefail

# Everything this script writes — the plaintext dump especially — is readable by its owner only.
umask 077

here="$(cd "$(dirname "$0")" && pwd)"

die() {
  echo "backup: $*" >&2
  exit 1
}

recipient=""
out_dir=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --recipient) recipient="${2:-}"; shift 2 ;;
    --out)       out_dir="${2:-}"; shift 2 ;;
    *)           die "unknown argument '$1'. Usage: backup.sh --recipient <age public key> --out <directory>" ;;
  esac
done

[[ -n "$recipient" ]] || die "--recipient is required: the age PUBLIC key the backup is encrypted to."
[[ -n "$out_dir" ]]   || die "--out is required: the directory the encrypted backup is written to."
[[ -d "$out_dir" ]]   || die "--out '$out_dir' is not a directory."
: "${PGDATABASE:?Set PGDATABASE to the database to back up.}"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
out_file="$out_dir/appplatform-$timestamp.tar.age"

# Two runs in the same second would share this name. The second refuses rather than replacing the
# first; the check is repeated when the file is published, for a run that started meanwhile.
[[ ! -e "$out_file" ]] || die "$out_file already exists; not overwriting it. Try again in a second."

# This run's own partial file, so concurrent runs never write to or clean up each other's.
partial_file="$(mktemp "$out_dir/.appplatform-$timestamp.partial.XXXXXX")"

# Plaintext lives only here, and only until the script exits — successfully or not.
work_dir="$(mktemp -d)"
snapshot_holder_pid=""

cleanup() {
  if [[ -n "$snapshot_holder_pid" ]]; then
    # Ending the holder aborts its read-only transaction, which releases the snapshot.
    kill "$snapshot_holder_pid" 2>/dev/null || true
  fi
  rm -rf "$work_dir"
  rm -f "$partial_file"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------
# One snapshot for the dump AND the row counts.
#
# The application keeps writing while a backup runs. If the counts were taken separately, a row
# committed between the dump and the count would appear in the manifest but not in the dump, and
# every restore of this backup would report data loss that never happened. So a transaction is
# held open here, its snapshot exported, and both pg_dump and the count query import it: they
# see exactly the same data. The holder must stay open until both are done, which is why it runs
# as a coprocess rather than a one-off psql call.
# ---------------------------------------------------------------------------------------------
echo "==> opening a snapshot on $PGDATABASE"
coproc SNAPSHOT_HOLDER { psql -X -q -t -A -v ON_ERROR_STOP=1; }
# bash sets SNAPSHOT_HOLDER_PID itself when it starts the coprocess named SNAPSHOT_HOLDER.
# shellcheck disable=SC2153
snapshot_holder_pid="$SNAPSHOT_HOLDER_PID"

echo "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT pg_export_snapshot();" >&"${SNAPSHOT_HOLDER[1]}"
read -r -t 30 snapshot <&"${SNAPSHOT_HOLDER[0]}" \
  || die "could not open a snapshot. Check the connection settings (PGHOST, PGUSER, PGDATABASE)."

# Large objects are refused rather than backed up. The application has none, and pg_restore wraps
# them in a COMMIT of its own, which would break restore.sh's single transaction (see step 4 there).
# Failing here finds the problem at backup time, not in the middle of a recovery. Counted in the
# dump's own snapshot, so the answer describes exactly what the dump will contain.
large_objects="$(
  {
    echo "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;"
    echo "SET TRANSACTION SNAPSHOT '$snapshot';"
    echo "SELECT count(*) FROM pg_largeobject_metadata;"
    echo "COMMIT;"
  } | psql -X -q -t -A -v ON_ERROR_STOP=1
)"
[[ "$large_objects" == "0" ]] \
  || die "$PGDATABASE holds $large_objects large objects, which these scripts do not support (restore.sh explains why)."

echo "==> dumping (snapshot $snapshot)"
# No --no-owner and no --no-privileges: the owners ARE the security model. A published view runs
# with its owner's privileges, so a dump that drops owners restores a database whose views either
# fail or grant the wrong rights (see database/privileges/00-roles.sql).
pg_dump --format=custom --snapshot="$snapshot" --file="$work_dir/database.dump"

echo "==> counting rows in the same snapshot"
{
  echo "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;"
  echo "SET TRANSACTION SNAPSHOT '$snapshot';"
  cat "$here/table-counts.sql"
  echo "COMMIT;"
} | psql -X -q -t -A -F $'\t' -v ON_ERROR_STOP=1 > "$work_dir/manifest.tsv"

[[ -s "$work_dir/manifest.tsv" ]] || die "the manifest is empty; is $PGDATABASE the right database?"

{
  echo "taken_at_utc: $timestamp"
  echo "database: $PGDATABASE on ${PGHOST:-local socket}"
  echo "server: $(psql -X -q -t -A -c 'SHOW server_version')"
  echo "pg_dump: $(pg_dump --version)"
  echo "tables: $(wc -l < "$work_dir/manifest.tsv" | tr -d ' ')"
} > "$work_dir/about.txt"

echo "==> encrypting to $out_file"
# Written under a hidden partial name and published only once complete, so an interrupted run can
# never leave a truncated file that looks like a good backup.
#
# Published with a hard link, not a rename: creating a link fails if the name already exists, in
# one atomic step. `mv -n` checks and then renames as two steps, so two runs that started in the
# same second could both pass the check and the second would replace the first.
tar -C "$work_dir" -cf - about.txt database.dump manifest.tsv \
  | age --recipient "$recipient" --output "$partial_file"
ln "$partial_file" "$out_file" \
  || die "could not publish $out_file (the reason is above); an existing file of that name is never replaced."
rm -f "$partial_file"

echo "==> done: $out_file"
cat "$work_dir/about.txt"
