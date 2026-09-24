# database/

Generated SQL, seed data, and the privilege scripts. **Schema source of truth is EF Core
migrations in each service, not the SQL here** — with one exception, below.

## Layout

```
privileges/
  01-roles-and-schemas.sql   run ONCE, as superuser, BEFORE the first migration
  02-grants.sql              run AFTER migrations, as superuser, idempotent
  99-verify.sql              run anytime; ZERO ROWS means the boundary holds
<schema>/                    per-migration and bootstrap scripts, generated
```

## The privilege scripts are hand-written, and that is deliberate

Everything else here is generated from EF migrations. The privilege scripts are not,
because EF has no model of a Postgres role and the boundary they express — which service
can read which schema — is an architectural decision, not a consequence of the entity
model. Generating them would mean deriving the security boundary from the thing it is
meant to constrain.

## Order matters, and getting it wrong fails silently

`ALTER DEFAULT PRIVILEGES` applies only to objects created **after** it runs. Run
`01-roles-and-schemas.sql` after the first migration and every table that migration
created is missing its grants — the application then fails with permission errors that
look like a misconfigured connection string. `02-grants.sql` repairs exactly that case,
which is why it exists at all.

New database:

```
psql -v ON_ERROR_STOP=1 -f privileges/01-roles-and-schemas.sql
# ... apply each service's migrations as its ap_<schema>_migrate role ...
# ... apply published views and SECURITY DEFINER functions as ap_owner ...
psql -v ON_ERROR_STOP=1 -f privileges/02-grants.sql
psql -f privileges/99-verify.sql        # expect zero rows
```

Passwords are never in these files. Roles are created able to log in with no password
set, so they cannot authenticate until one is issued out of band.

## Two things a migration must do that no test will catch

- **Grant what it creates.** A published view or function nobody can reach is a broken
  deploy that every unit test passes.
- **Create cross-schema objects as `ap_owner`.** A view executes with its owner's
  privileges — that is the mechanism the whole boundary rests on. Created by a migration
  role instead, it either cannot see what it reads or hands the consumer the wrong rights.

## Verification

`99-verify.sql` asserts **negatives** — that roles cannot do things. Asserting positives
would pass against a database where every role can read everything, which is the failure
being guarded against. `privileges.tests` runs the same assertions against a throwaway
PostgreSQL container in CI.
