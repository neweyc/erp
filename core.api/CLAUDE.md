# core.api — the required spine

Owns schemas `core`, `identity`, and the published `core_v1` views.
Root rules in `../CLAUDE.md` apply; this file covers what is specific to core.

## What belongs here

Companies, employees, departments, locations, job titles, users, roles, sessions,
tenant settings, terminology, core audit. That is the whole list.

**What does NOT belong here, however tempting:** training, certifications, compliance
requirements, discipline, complaints, tickets, invoices, anything with a workflow. Core
is "who works here, in what structure, with what access." If a feature has a lifecycle,
a status, or a document, it is an app.

The test: would a customer who bought only the ticket system still need this? If no, it
is an app.

## Core owns the published contract

`core_v1.*` views are the only thing other projects may read. They are part of core's
migrations and part of core's public API — changing one is a breaking change to every
app, so treat a view edit with the same care as a route signature.

- Thin and denormalized: ids, display names, status, the FKs apps need to filter by.
- **Never expose an encrypted column.** Ciphertext cannot be filtered, sorted, or grouped
  and would surface to apps as opaque base64.
- Carry `tenant_id` and `company_id` so consumers apply their ordinary filters.
- Include enough to render a reference without a second call: an app showing "assigned
  to" should not need to join three views.
- Additive changes only. Removal or rename means `core_v2.*` alongside `core_v1.*` until
  every app has moved, then a deliberate deletion.

A contract test asserts the shape of every published view against a golden snapshot, so
an incidental model change cannot silently alter what apps see.

## Employees

- `Employee.Status`: active / on_leave / terminated. **Terminated is not deleted** —
  soft delete is for records created in error only. Terminated employees keep their
  history and stay visible in apps that reference them historically, but are excluded
  from active counts. Every new employee-facing query must decide explicitly how it
  treats terminated employees, and say so.
- `identity.user.employee_id` is what makes termination cut access: terminating an
  employee deactivates the linked account and revokes its sessions. An unlinked account
  survives its employee's departure, which is the bug this link exists to prevent.
- **"Invite this employee" is a first-class operation** on the employee record, linking
  by employee **id**. It is the normal onboarding path: create the employee today, grant
  access tomorrow.
- Inviting a bare *address* that belongs to a non-terminated employee is **rejected**,
  and points at the invite-this-employee action instead. Silent email-based linking stays
  forbidden — matching on an address guesses at identity, and the rejection is only
  reasonable because the explicit operation exists. Never ship one without the other.
- PII (phone, address, emergency contact) is encrypted at rest via the field converter.
  Feature code only ever sees plaintext.

## Companies

One per tenant, created at provisioning. See the root CLAUDE.md for why the column
exists and why there is no UI for it. `CompanyId` is assigned automatically exactly as
`TenantId` is — **feature code never sets it** while there is one company per tenant.
When multi-company is enabled, that assignment becomes explicit in one place, not
scattered through handlers.

## Catalogs (departments, job titles, locations)

Tenant-scoped, `Name`/`Active`/`SortOrder`, **deactivate — never delete**, admin editor
in settings. Two lessons carried from EMS, both learned the hard way:

- Where a denormalized display copy of a catalog name sits beside the FK, the FK is the
  source of truth and the **only** thing safe to group or order by. Write both together
  through one assignment helper; never set one alone. A rename re-stamps every holder in
  the same `SaveChanges`.
- **Catalog pickers are filter-and-pick, never free text.** Typing narrows the list; it
  does not enter a value, so a typo dead-ends at "No matches" instead of becoming a row.
  A control that offers to create whatever was typed does not catch typos, it ratifies
  them — and a typo blessed as a catalog row is worse than loose text because it looks
  deliberate.

## Terminology

Tenants rename domain terms to match their business ("Employee Number" -> "Badge
Number"). Code-defined keys with default singular/plural forms; tenants override values
only. Resolved through the shell's `useTerm()`. **Never hardcode a renamable term in UI
or help copy.** Registry mirrored in the shell — keep in sync.
