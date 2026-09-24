# Backlog

Prioritized. Keep current as work ships. Nothing below is scheduled — this is order, not
dates.

## Slice 0 — foundations (no customer-visible feature)

1. Solution, workspaces, path-filtered CI, per-project tagging.
2. `packages/tenancy` — `ITenantScoped`, query filter, stamping, cross-tenant guard,
   with the integration tests that prove all three.
3. `packages/auth` — cookie scheme, server-side sessions revalidated per request, roles
   and policies. Sole owner of the `identity` schema.
4. `BoundaryTests` — schema whitelist, identity-mapping rule, no cross-app references.
   Written **before** the second project exists, so it never has to be retrofitted.
5. `packages/audit`, `packages/encryption`.

## Slice 1 — core spine

6. Companies (one per tenant, no UI), employees, departments, locations, job titles.
7. `core_v1` published views plus the golden-snapshot contract test.
8. Users, invitations, role assignment, the `user.employee_id` link and termination
   cutting access.
9. Tenant settings and terminology.
10. Shell: session, nav, app registry, terminology provider, empty state.

## Slice 2 — platform control plane

11. Tenants: create, suspend, resume, retire, enforced at sign-in and per request.
12. Provisioning via core's internal endpoint — tenant + company + admin invite in one
    transaction, send-then-commit.
13. Entitlements: `platform.tenant_app`, `.RequireApp()` filter, licensed apps in the
    session payload, shell gating with dynamic import.
14. Operator auth with mandatory MFA; operator audit.
15. Error feed (metadata only).

## Slice 3 — tickets, the first licensed app

16. Own schema and migrations; proves per-project migration ownership.
17. Tickets with requester/assignee read from `core_v1` **plus display snapshots**.
18. Comments, attachments, audit trail.
19. Tenant-configured status and category wording over fixed enums.
20. The "unlicensed tenant gets 403" test, and the e2e journey: provision -> invite ->
    sign in -> create employee -> license tickets -> raise and close a ticket.

## Later, unordered

- Billing records and invoicing.
- Key rotation.
- Backup/restore/drill scripts and the runbook.
- ERP apps (GL, AP/AR, procure-to-pay, inventory) and multi-company activation.
- Reporting engine.
- Email-to-ticket, SLAs, customer portal — deliberately out of tickets v1.
