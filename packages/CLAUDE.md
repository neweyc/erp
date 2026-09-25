# packages — shared libraries

Root rules in `../CLAUDE.md` apply.

## Why these exist

Tenancy, auth, audit, encryption, storage, email, and the UI kit have now been written
twice (redshift -> EMS). A third copy-paste is the outcome this directory exists to
prevent.

## The rule that keeps them from re-coupling releases

Shared code is a **versioned dependency**, not a project reference across a deploy
boundary. A project reference means a change here forces every app to rebuild and ship
together — which is precisely the coupling the schema-per-app layout exists to remove.
Publish to an internal feed; consumers pin a version and upgrade deliberately.

**The rule activates at the first independent app release.** Until then the supported
matrix is "everything from the same commit" (`docs/compatibility.md`), project references
are correct, and building a package feed first would delay the first working journey for
a coupling problem that does not exist yet.

## What qualifies

A package is infrastructure that every project needs and no project owns: a cross-cutting
mechanism, declared once and enforced automatically. **Business logic never goes here.**
If it knows what an employee or a ticket *means*, it belongs in core or an app.

The test that catches most mistakes: could a brand-new app use this on day one without
knowing what the app does? If no, it is not a package.

## Contents

- `tenancy` — `ITenantScoped`, query filters, stamping, cross-tenant guard.
- `auth` — cookie scheme, server-side session revalidation, MFA, roles, policies. **The
  only assembly permitted to map the `identity` schema.**
- `entitlements` / `entitlements-ts` — app constants and the `.RequireApp()` filter.
  Mirrored pair; keep in sync.
- `audit` — `IAuditable` and `AuditedDbContext`, which stages audit rows on every save. Each
  service owns its own `audit_log` in its own schema (core's covers `identity` too). See
  `docs/audit.md`.
- `encryption` — AES-256-GCM field converter. Encrypted columns get no max length and
  **cannot be searched or filtered in SQL**; cap plaintext length in handler validation.
- `storage` — `IFileStore`. Never touch the filesystem from feature code.
- `email` — `IEmailService`, transport chosen by which credential is present. Sending
  never happens inside a request transaction.
- `outbox` — the transactional outbox table and its worker, serving **email and domain
  events over one mechanism**. A record and its pending notification commit together; the
  worker delivers with retries. Delivery is at-least-once, so anything it triggers must be
  idempotent. Webhooks are a transport on this, never a second delivery system.
- `ui-kit` — shadcn components, DataTable, dialogs, form primitives.

Every package needs a changelog. A consumer deciding whether to take an upgrade has
nothing else to read.
