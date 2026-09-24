# Database privileges

**Status:** design, v0.2 — gates the first deployment that holds customer data.

EF model tests constrain the code in this repo. They do not constrain raw SQL, a
compromised process, or anyone with the connection string. **PostgreSQL grants are what
make the schema boundary real.** Without them, every process connected to the database
can read everything in it, and the platform's "boundary" is a coding convention.

## The mechanism that makes a published view a boundary

A view executes with its **owner's** privileges, not the caller's. That single fact is
what lets `ap_tickets_rt` read `core_v1.employee` while having no grant whatsoever on
`core.employee`.

**Two things this requires that are easy to miss, and both were found by running the
scripts against a real PostgreSQL rather than by reading them:**

1. **The view's owner must be able to read the tables the view reads.** A table is owned
   by whoever *created* it — the migration role — not by `ap_owner`. So a published view
   owned by `ap_owner`, correctly granted to the consumer, still fails, and the error
   names the *underlying* table. It reads as a missing grant on a table the consumer is
   deliberately not supposed to have one on, which sends you looking in the wrong place.
   Fixed by making `ap_owner` a member of every migration role, so it inherits read on
   whatever they create — including tables that do not exist yet.
2. **A table grant is inert without `USAGE` on the schema holding it.** The provisioning
   exception grants core `INSERT` on `platform.tenant`; without `GRANT USAGE ON SCHEMA
   platform`, that does nothing and the failure is `permission denied for schema
   platform`. USAGE conveys no access to anything *in* the schema, so granting it does not
   widen the exception: core still reaches `platform.tenant` and no other platform table.

It follows that:

- **Published views are owned by `ap_owner`** and created by a migration running as
  `ap_owner` — not by the owning service's migration role, which by design cannot see the
  schemas some views read across.
- **`security_invoker` must stay OFF** (the default) on every published view. Setting it
  is an increasingly common reflex on PG15+, and here it would resolve permissions as the
  *caller* and collapse the entire boundary. A test asserts it is off.
- The rule inverts for the underlying tables: the fewer roles that can read them, the
  better. Only `ap_owner` and the owning service.

## Roles

One database, one owner, one runtime role per deployable, one migration role per schema.

| Role | SELECT | INSERT/UPDATE/DELETE | DDL |
|---|---|---|---|
| `ap_owner` | all | all | all — **never used by a running process**; owns published views and SECURITY DEFINER functions |
| `ap_platform_rt` | `platform` | `platform` | none |
| `ap_core_rt` | `core`, `identity`, `core_v1`, **`platform.tenant`** | `core`, `identity`, **INSERT only on `platform.tenant`** | none |
| `ap_tickets_rt` | `tickets`, `core_v1` | `tickets` | none |
| `ap_<schema>_migrate` | its schema | its schema | its schema only |

Rules that follow, and the reason each exists:

- **A runtime role never has DDL.** An application bug cannot drop a table, and a
  compromised process cannot rewrite the schema.
- **A runtime role is granted views and functions, not the tables behind them.** Bypassing
  a published contract fails at the database rather than in review.
- **`ap_platform_rt` has no grant on `core`, `identity`, or any app schema.** This is the
  platform boundary. The missing encryption key protects encrypted columns only; names,
  emails, and ticket titles are plaintext and this grant is the only thing that keeps
  them unreadable.
- **No role is a superuser and none owns the objects it uses**, so revoking is possible
  and ownership cannot be used to re-grant.
- `REVOKE ALL ON SCHEMA public FROM PUBLIC`, and default privileges set per schema so a
  newly created table is not accidentally world-readable.

### The provisioning grant, stated narrowly

Provisioning delegates to core because the tenant row, its company, the kit seeds, and the
admin invite must commit in **one transaction**, and a transaction cannot span two
services. So core writes the tenant row, which means `ap_core_rt` needs a grant on a
platform table — the one deliberate exception to the boundary.

It is split by **operation**, and the split carries a meaning worth keeping:

- `ap_core_rt` has **SELECT and INSERT** on `platform.tenant`. It can create a tenant and
  read its own tenant's status.
- It has **no UPDATE or DELETE**. Lifecycle — suspend, resume, retire — belongs to the
  platform alone. Core can bring a tenant into existence; only the operator can change
  what it is allowed to do.
- The grant is on `platform.tenant` **only**, never `platform`. No entitlement row,
  billing record, operator account, or audit entry is reachable from core.

## Keyed lookups use functions, not views

Views are right where a consumer needs to **join, filter, and page** — that is why
`core_v1` exists at all. They are wrong where the consumer needs **one row it already
holds the key for**, because a grant on a view is a grant to read all of it, and
`SELECT * FROM identity_v1.session_context` would enumerate every live session in every
tenant.

So session lookup and session touch are **functions**, not views:

```
identity_v1.session_context(p_session_id uuid) returns table (...)   -- one row, or none
identity_v1.touch_session(p_session_id uuid) returns void
```

Both are `SECURITY DEFINER`, and that carries mandatory hygiene:

- `SET search_path = pg_catalog, pg_temp` on the function. Without it, a caller who can
  create objects can shadow an unqualified name inside the body and have it run as the
  owner.
- Owned by `ap_owner`, which is not a role any process connects as.
- Globally revoke default function EXECUTE for `ap_owner` (without `IN SCHEMA`);
  per-schema revocation cannot remove PostgreSQL's global default. Revoke EXECUTE on
  existing functions from PUBLIC, then grant EXECUTE to the specific roles.
  Functions are executable by PUBLIC by default; skipping the revoke grants them to every
  role in the cluster.
- Granted to **customer** API roles only — `ap_core_rt`, `ap_<app>_rt`. **Never
  `ap_platform_rt`**: operator sessions live in their own platform tables, and granting
  the platform a way to resolve tenant sessions would undo its isolation.

## Migrations

Applied as the migration role for that schema, never by the runtime role, never
automatically on startup. Chris applies SQL by hand.

Two things a migration must do in the same script as the object it creates, because
neither is visible to a unit test:

- **Grant it.** A published view or function nobody can reach is a broken deploy.
- **Set ownership** for anything that crosses schemas — published views and SECURITY
  DEFINER functions are created as `ap_owner`, in a script section that says so.

## Verification

`database/privileges.sql` is generated and checked in. An integration test connects **as
each runtime role** and asserts the negatives:

- `ap_platform_rt` selecting `core.employee` raises insufficient privilege.
- `ap_tickets_rt` selecting `core.employee` raises insufficient privilege, while
  `core_v1.employee` succeeds.
- `ap_core_rt` updating `platform.tenant` raises insufficient privilege, while INSERT
  succeeds.
- `ap_platform_rt` executing `identity_v1.session_context` raises insufficient privilege.
- No published view has `security_invoker` set.

Asserting only the positives would pass against a database where everyone can read
everything, which is exactly the failure this file exists to prevent.

`privileges.tests` runs all of this against a throwaway container on every build: a
21-case access matrix stating role, statement, and expected outcome, plus a break/repair
test per rule that breaks the rule, asserts the verifier reports it, repairs it, and
asserts the report clears. **The verifier reports only violations, so a clean database and
a broken verifier both produce zero rows** — those break/repair cases are the only thing
that tells them apart. Requires Docker.

## Open

- Whether apps eventually need their own database rather than a schema; grants make the
  boundary real but one cluster remains one blast radius.
- Row-level security as defence in depth behind the EF tenant filter. Attractive, and it
  changes how connection pooling and tenant context work — not free.
- Secret storage and rotation for the connection strings themselves.
- Whether the provisioning exception should become a SECURITY DEFINER
  `platform.provision_tenant(...)` instead of an INSERT grant. Tighter, at the cost of
  moving row construction into SQL where the rest of it is EF.
