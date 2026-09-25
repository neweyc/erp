# Audit

Who changed what, and when — recorded automatically, in the same transaction as the change.
Built as `packages/audit`. Closes D11, and is the first half of what a ledger needs (the other is
the append-only guard, D10).

## The rule

**Feature code never writes an audit row.** An entity opts in by implementing `IAuditable`; its
context derives from `AuditedDbContext`; every Created / Updated / Deleted of that entity then
produces a row in the owning service's `audit_log` during `SaveChanges`. One save, one transaction:
the change and its audit row commit together or not at all.

`BoundaryTests` makes the opt-in the default: every tenant-scoped, publicly identified entity must
be `IAuditable`, and every context holding one must derive from `AuditedDbContext` (checked on the
context's type — mapping the audit table is not what writes rows). A new entity that should not be
audited has to say so in the allowlist, where a reviewer sees it.

**"Old" values come from the database, with the row locked.** For an update or delete the row is
locked (`SELECT … FOR UPDATE`, scoped to the tenant) and its stored values read, inside the same
transaction the write commits in — the save opens one when the caller has none. Both halves matter:

- *From the database, not EF's change snapshot.* The snapshot is only true for an entity this context
  loaded: attach a detached entity with `Update()` and its originals equal whatever the caller
  supplied, so the history would show no change, or record a caller's claim as what was deleted.
  The row's public id is taken from the stored row for the same reason, and cannot be changed.
- *Locked through the write.* Otherwise another writer can change the row in between, and the
  history records a "before" that is not what was overwritten.

Cost: one keyed lock-and-read per updated or deleted auditable entity. A row that is missing or
belongs to another tenant is refused as a concurrency conflict — what handlers already answer 409 —
before anything from it is read into an audit row.

**A failed save withdraws its rows.** Otherwise a corrected retry would commit a row for the change
that failed as well as the real one.

**Two shapes are refused rather than half-supported**, by `BoundaryTests`: an owned type on an
auditable entity (its changes live on an entry that is not auditable) and a non-key value the
database generates (not known when the row is built). A foreign key to a row added in the same save
is refused at runtime for the same reason: save the referenced row first.

## What is audited

| Audited | Not audited, and why |
|---|---|
| `Company`, `Employee`, `User`, `Ticket` | `Session` — touched on every request; its own table is already the record |
| | `UserToken` — a credential hash; its effect is recorded on the `User` it activates |
| | Outbox rows — delivery bookkeeping, not business state |
| | `Tenant` — owned by the platform; operator actions go to `platform.audit_log` |

## What a row holds

| Column | Meaning |
|---|---|
| `occurred_at` | Clock time of the save |
| `actor_kind` | `User`, `ApiKey`, or `System` |
| `actor_id` | The principal id — a user id or an API key id. Null for `System` |
| `actor_name` | For `System` only: which process, e.g. `provisioning` |
| `action` | `Created`, `Updated`, `Deleted` |
| `entity_type` | The table name, e.g. `employee` |
| `entity_id` | The **public id**, so the row still means something after the entity is gone and matches what integrations hold |
| `changes` | JSON: `{ "column": { "old": …, "new": … } }`. `old` is null on create, `new` on delete. An update lists only columns whose value changed |

Keys, `tenant_id` and `public_id` are left out of `changes` — they are the row's identity, not a
change to it.

**Redaction.** A property marked `[AuditRedacted]` records that it changed, never its value
(`"[redacted]"`). `User.PasswordHash` is the first. Any future encrypted column must be redacted
too: copying ciphertext's plaintext into an audit row would undo the encryption.

## The actor is required

**Saving an auditable change with no actor throws `AuditActorMissingException`.** The same stance
as tenancy: failing closed is recoverable, a row reading "someone" is not.

- **In a request** the actor is the authenticated `Caller` — a user or an API key — supplied by
  `CallerContext`, the same object that supplies the tenant. Feature code does nothing.
- **Before a session exists** the path declares its actor through `IAuditActorScope`, exactly as it
  declares its tenant through `IBackgroundTenantScope`:
  - accepting an invitation, and a password rehash at sign-in: the user themselves;
  - provisioning: `System("provisioning")`;
  - the e2e seed: `System("e2e-seed")`.
- `UseActor` is refused once a caller exists, so a request cannot re-attribute its own writes.

## Append-only, twice

1. **Grant.** Runtime roles hold `SELECT, INSERT` on `audit_log` — no `UPDATE` (table or column),
   `DELETE` or `TRUNCATE`. `02-grants.sql` revokes them after the bulk grant, finding every
   `audit_log` and every `ap_%_rt` role by name, so a new app is covered once `02` runs after its
   first migration, as the runbook requires. The whole of `02` is one transaction, because its bulk
   re-grant would otherwise leave the logs writable until the revoke ran. `99-verify.sql` reports
   a regression, column-level grants included. This is the defence that holds against code that is
   not in this repo.
2. **Code.** `SaveChanges` throws if an `AuditEntry` is ever modified or deleted, so the mistake
   surfaces in a unit test rather than as a permission error in production.

## What bypasses it

The same two paths that bypass tenancy: raw SQL and `ExecuteUpdate`/`ExecuteDelete`. Neither
reaches `SaveChanges`, so neither is audited. Today the only uses touch sessions and the outbox,
neither audited. Any future use on an auditable entity must say in a comment why the missing row
is acceptable.

## Deliberately not built

- **Reading it.** No endpoint or screen yet; the rows exist to be read when one is needed.
- **Events that are not entity changes** — a successful or failed sign-in, a sign-out. A password
  change or an invitation accepted IS recorded, as an update to the `User`. Sign-in attempts are
  a security log rather than a data history, and belong with rate limiting (M2 item 18).
- **Retention and erasure.** Append-only and PII-bearing (an employee's email change records both
  addresses) are in tension with a right-to-erasure request. Logged in `docs/open-questions.md`.
- **Moving operator audit onto this.** `platform.audit_log` already exists and is written
  explicitly by each operator handler. It is separate by design (operator actions are not tenant
  data) and stays as it is; the platform has no tenant-scoped entities for this package to audit.
