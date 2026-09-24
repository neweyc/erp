# Database privileges

**Status:** design, v0.1 — gates the first deployment that holds customer data.

EF model tests constrain the code in this repo. They do not constrain raw SQL, a
compromised process, or anyone with the connection string. **Postgres grants are what
make the schema boundary real.** Without them, every process connected to the database
can read everything in it, and the platform's "boundary" is a coding convention.

## Roles

One database, one owner, one runtime role per deployable, one migration role per schema.

| Role | SELECT | INSERT/UPDATE/DELETE | DDL |
|---|---|---|---|
| `ap_owner` | all | all | all — **never used by a running process** |
| `ap_platform_rt` | `platform`, `tenant` | same | none |
| `ap_core_rt` | `core`, `identity`, `core_v1`, `identity_v1` | `core`, `identity` | none |
| `ap_tickets_rt` | `tickets`, `core_v1`, `identity_v1` | `tickets` | none |
| `ap_<schema>_migrate` | its schema | its schema | its schema only |

Rules that follow, and the reason each exists:

- **A runtime role never has DDL.** An application bug cannot drop a table, and a
  compromised process cannot rewrite the schema.
- **A runtime role is granted views, not the tables behind them.** `ap_tickets_rt` has
  SELECT on `core_v1.employee` and **no** grant on `core.employee`, so bypassing the
  published contract fails at the database rather than in review.
- **`ap_platform_rt` has no grant on `core`, `identity`, or any app schema.** This is the
  platform boundary. The missing encryption key protects encrypted columns only; names,
  emails, and ticket titles are plaintext and this grant is the only thing that keeps
  them unreadable.
- **No role is a superuser and none owns the objects it uses**, so revoking is possible
  and ownership cannot be used to re-grant.
- `REVOKE ALL ON SCHEMA public FROM PUBLIC` and default-privilege defaults set per
  schema, so a newly created table is not accidentally world-readable.

## Migrations

Applied as the migration role for that schema, never by the runtime role, never
automatically on startup. Chris applies SQL by hand.

A migration that adds a published view must also grant it, in the same script. A view
nobody can select from is a broken deploy that unit tests will not catch.

## Verification

`database/privileges.sql` is generated and checked in. An integration test connects **as
each runtime role** and asserts the negatives — `ap_platform_rt` selecting
`core.employee` must raise insufficient privilege. Asserting only the positives would
pass against a database where everyone can read everything, which is exactly the failure
this file exists to prevent.

## Open

- Whether apps eventually need their own database rather than a schema; grants make the
  boundary real but one cluster remains one blast radius.
- Row-level security as defence in depth behind the EF tenant filter. Attractive, and it
  changes how connection pooling and tenant context work — not free.
- Secret storage and rotation for the connection strings themselves.
