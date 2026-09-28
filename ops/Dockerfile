# The tools ops/backup.sh and ops/restore.sh need: psql, pg_dump and pg_restore, bash, and age.
#
# The PostgreSQL major version is pinned to the server's. pg_dump refuses a server NEWER than
# itself, so when the database is upgraded this line moves with it — before the first backup
# after the upgrade, not after one fails.
#
# The image holds no scripts. They are mounted or copied in from this repository at run time, so
# the image cannot drift from the scripts it runs. The e2e harness also runs its databases from
# this image, which is what lets the restore drill run the scripts inside them.
FROM postgres:16-alpine

RUN apk add --no-cache age
