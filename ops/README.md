# ops/

Backup and restore of the app-platform database (Milestone 2, items 13 and 15).

| File | What it is |
|---|---|
| `backup.sh` | An encrypted, consistent backup of the whole database: one `.tar.age` file |
| `restore.sh` | Restores a backup into an EMPTY database and proves the result |
| `after-restore.sql` | Revokes every session; run by `restore.sh` |
| `table-counts.sql` | Every table's row count; the backup's manifest and the restore's check |
| `Dockerfile` | The tools both scripts need: psql, pg_dump, pg_restore, bash, age |

**A restore that has never been exercised is a hypothesis.** This one is exercised on every CI
run: `e2e/specs/restore.spec.mjs` backs up the database the browser journey has filled, restores
it with these scripts, starts the APIs against the copy, and checks it through them. A drill
against the real hosted database is Stage 2 and has not happened yet.

## What a backup covers

The database is currently the only thing that holds customer data, so one backup is the whole
of it:

- **File storage: none yet.** `packages/storage` is not built. When it is, its volume must be
  backed up *together* with the database (CLAUDE.md), and this section must change the same day.
- **Field encryption key: none yet.** `packages/encryption` is not built. When it is, a backup
  is unreadable without `Encryption:FieldKey`, and the key's custody and recovery (item 14) must
  be settled before it protects anything. Never store that key alongside the backups.
- **Data Protection key ring** (`DataProtection:KeyPath`): signs cookies only. Losing it signs
  everyone out and loses no data, so it is not backed up.
- **Large objects: refused.** The application stores none, and `pg_restore` commits them in a
  transaction of its own, which would break the restore's single transaction. `backup.sh` fails if
  the database holds any, and `restore.sh` refuses an archive containing them.
- **Roles** are not in a dump; `restore.sh` creates them from `database/privileges/00-roles.sql`.
  Their passwords are not in anything: set them out of band after restoring to a new server.

## Keys

Backups are encrypted with [age](https://age-encryption.org) to a public key. Make the pair once,
on a machine that is not the database host:

```
age-keygen -o appplatform-backup-identity.txt   # the PRIVATE key; prints the public key
```

`backup.sh` needs only the public key (`age1...`), so the machine taking backups cannot read
them. The identity file is needed to restore, and losing it loses every backup made with it.
**Where it is kept and who can retrieve it is not decided yet** (backlog item 14).

## Taking a backup

```
PGHOST=... PGUSER=... PGDATABASE=appplatform \
  ops/backup.sh --recipient age1... --out /path/to/backups
```

Connect as a role that can read every schema. The result is
`appplatform-<UTC timestamp>.tar.age`. Copy it off the database host. **The schedule, the
retention, and where copies go are not decided yet** (Stage 2).

## Restoring

```
createdb appplatform_restored
PGHOST=... PGUSER=<superuser> PGDATABASE=appplatform_restored \
  ops/restore.sh --identity appplatform-backup-identity.txt appplatform-<timestamp>.tar.age
```

The script refuses a database that is not empty. The data and the session revocation commit
together or not at all, so there is never a committed copy with working restored sessions. When it
prints `restored and verified`, every table matches the backup's row counts and `99-verify.sql`
found nothing. If it stops before that, it says `DID NOT FINISH`: drop that database and restore
into a new one.

### After the script

1. **Start core.api with `Outbox__DeliveryPaused=true`.** The restored outbox includes messages
   the live system had already sent after the backup was taken; unpaused, they are all sent
   again at startup. Paused, new invitations are staged and kept.
2. **Review what is pending** before resuming:
   `SELECT id, destination, payload->>'kind', created_at FROM core.outbox_message WHERE status = 'Pending' ORDER BY created_at;`
   Anything created before the backup point may already have been sent by the live system.
   Re-sending a tenant admin invitation sends the same link again. Decide about each message,
   then restart core.api without the setting.
3. **Set passwords for the runtime roles** if this is a new server, and point each API at it.
4. **Tell customers what the backup point means for them.** Everything after it is gone,
   including its audit rows. Everyone must sign in again. And the restore brings back the old
   state of things that were deliberately changed since:
   - a password changed after the backup point is the old one again;
   - a user deactivated after it is active again;
   - a role reduced after it has its old role.
   - an invitation accepted after it is unaccepted again, and its link works again until the
     original expiry (7 days from issue). This is deliberate: the account is back to Invited with
     no password, so the invitee must accept again, and nothing can reissue an invitation yet.
     If a link is known to have leaked, expire that token by hand before the copy takes traffic.
   Nothing in the database records these once the backup point has passed, so they are redone
   from the customer's own records. Treat anyone whose password was changed because it leaked
   as exposed until they change it again.
5. **The same for operators.** `platform.platform_user` rolls back too: an operator's changed
   password or removed access returns. Reconcile it from your own records before the console is
   used.
6. **Tenants provisioned after the backup point are gone**, and so are the idempotency records
   that would recognise a retry. A caller that already received a tenant id and retries with its
   original key gets a NEW tenant, not the old answer. Before provisioning resumes, list what was
   provisioned after the backup point (from the operator's own records) and settle each one.

## Not yet decided (Stage 2)

Recovery point and time objectives, the backup schedule, off-site storage, who holds the
identity file, and a timed drill against the managed database. These need a hosting provider.
Also: some managed providers do not grant a true superuser, and `restore.sh` must assign
ownership to `ap_owner`. Confirm that works on the chosen provider before relying on it.
