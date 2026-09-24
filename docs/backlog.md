# Backlog

Prioritized. Keep current as work ships. Nothing below is scheduled — this is order, not
dates.

The ordering rule: **each milestone is thin but end-to-end.** An earlier draft of this
file completed each layer before starting the next, which put the first working journey
twenty items away. Build a narrow vertical slice of everything, then widen.

---

## Milestone 1 — the walking skeleton

**One journey, working:** provision a tenant → invite an admin → sign in → license
tickets → create one ticket, with a second tenant proving isolation.

Deliberately thin: one employee with a name and an email, no catalogs, no terminology,
no MFA polish, no error feed, no operator niceties, project references instead of a
package feed. Everything ships from one commit.

**Time budget: 40 hours.** The September viability work found no validated demand for a
catalog or managed delivery and capped speculative platform work at 20 hours; this is a
deliberate raise, not an oversight. Stop at the boundary and decide to continue, revise,
or shelve — rather than drifting.

**Success criteria — all of them, or the milestone is not done:**

1. The journey runs end to end in Playwright against a real stack.
2. Tenant B cannot read, modify, or **reference** tenant A's data, proved by tests.
3. A tenant without the tickets entitlement gets 403 from the API, not merely a hidden
   nav item.
4. `BoundaryTests` passes, and an integration test connecting **as each Postgres runtime
   role** proves the negatives (`docs/database-privileges.md`).
5. A restore from backup produces a working system (see Milestone 2 — this is the drill,
   not the full runbook).

### Work

1. Solution, workspaces, one CI pipeline. Path filters and per-project tags can wait —
   nothing deploys independently yet.
2. `packages/tenancy` — `ITenantScoped`, query filter, stamping, cross-tenant guard, and
   **composite tenant-carrying foreign keys** with the reference-isolation tests.
3. `BoundaryTests` — written **before** the second project exists, so it is never
   retrofitted onto code that already violates it.
4. `database/privileges.sql` — roles and grants, with the negative tests. Cheap now;
   invasive once three services share a connection string.
5. `packages/auth` per `docs/auth-and-access.md` — sessions, `identity_v1.session_context`,
   specific failure codes, CSRF, cookie isolation. **Write the doc's open items down
   before coding.** Include the **principal** abstraction now: cookie and API-key schemes
   resolving to one authorization model (`docs/integration.md` §2.3). Retrofitting a
   second principal kind through every policy check is the expensive version.
6. **Public opaque ids** (`emp_...`, `tkt_...`) on every externally-referenceable entity,
   in the first migration that creates it. Irreversible once a customer stores one.
7. `packages/outbox` — one outbox **per owning schema**, serving email and domain events,
   with the event and its per-subscription delivery rows kept separate from the start.
   Every domain write emits an event from the first feature, even with nothing subscribed:
   the reason is retrofit cost across every existing handler, not history
   (`docs/integration.md` §2.2). Decide the retention window here.
8. Core, minimal: tenant, one company, employee (name, email, status), the
   `user.employee_id` link, `core_v1.employee`, "invite this employee".
9. Platform, minimal: create tenant with an **idempotency key**, suspend/resume,
   `tenant_app` entitlement rows, operator sign-in.
10. Shell, minimal: sign in, app registry with one app, entitlement gating with dynamic
    import.
11. Tickets, minimal: create, list, assign to an employee read from `core_v1` **with a
    display snapshot**, close. Own schema, own migrations.
12. The e2e journey, the isolation tests, the 403 test.

**Not in M1:** the public API, webhooks, scopes, and developer docs. Only the three
irreversible decisions above land now — the surface itself waits for a customer who wants
it (`docs/integration.md` §1).

---

## Milestone 2 — safe to hold real data

**Gates the first customer deployment.** Nothing below is optional once someone else's
data is in the database. Expand/contract migrations and an application rollback rehearsal
against the upgraded schema are required here, even with coordinated releases.

13. Backup of database **and** file storage together, restored and **verified working**,
    not merely completed. A restore that has never been exercised is a hypothesis.
14. **Encryption key custody and recovery.** Losing `Encryption:FieldKey` loses the data
    permanently. Where the key lives, who can retrieve it, and how recovery is rehearsed
    must be written down and tested before the key protects anything real. Key
    *rotation* can wait; key *recovery* cannot.
15. Restore drill script and runbook.
16. **Mandatory operator MFA, enforced.** An operator reaches the control plane for every
    tenant, so an unprotected operator account is a larger exposure than most of this
    milestone. Enforcement belongs to the gate; enrolment polish and the recovery UX can
    wait for M3.
17. Per-project audit logging and the operator error feed (metadata only).
18. Rate limiting on anonymous auth endpoints and uploads.

There is an uncomfortable symmetry worth noticing: the September work concluded the
sellable service was a recovery check. Being unable to restore this platform would be
disqualifying.

---

## Milestone 3 — widen

19. Catalogs: departments, job titles, locations, with rename fan-out and pick-only
    controls.
20. Terminology overrides and the settings editor.
21. Tenant-configured ticket status and category wording over fixed enums.
22. Comments, attachments, ticket audit trail.
23. Operator MFA enrolment polish and recovery UX; operator audit; tenant lifecycle
    screens.
24. Contextual help.

---

## Milestone 3.5 — integration surface

Built when a customer asks for it, not before. The groundwork is already in M1, so this
is additive rather than invasive.

25. API keys: issue, scope, rotate, revoke, with per-key audit and last-used.
26. `/api/public/v1/` — a curated read surface over employees and tickets, OpenAPI
    generated and published, scoped per app and entitlement.
27. Webhooks over the existing outbox: endpoint registration, HMAC signing, retry with
    backoff, dead-letter, auto-disable. Per-subscription delivery rows, not a flag on the
    event. **SSRF defences and restricted worker egress ship with the first endpoint, not
    after.**
28. Per-tenant integration log — sent, received, retried, replayable. Ships with the first
    webhook; it is what stops integration support consuming evenings.
29. Self-service event replay from the retention window.

Check first whether the prospect actually wants **SSO** — it is frequently the real ask
and worth more per hour than a data API.

## Milestone 4 — independent release

Activates the rules currently dormant.

30. Path-filtered CI, per-project version tags, per-project changelogs.
31. Shared packages published to an internal feed; consumers pin versions.
32. Route version segments enforced; `docs/compatibility.md` filled in with a real
    support policy.
33. Extend the Milestone 2 rollback rehearsal to independently supported app versions.

---

## Later, unordered

- Billing records and invoicing.
- Operator-driven key rotation.
- ERP apps (GL, AP/AR, procure-to-pay, inventory) and multi-company activation.
- Reporting engine.
- Email-to-ticket, SLAs, customer portal — deliberately out of tickets v1.
- Impersonation for support, if it can be designed safely.
- Inbound event consumption, SCIM provisioning, scheduled SFTP export, embedded widgets.
- Sandbox tenants for customer integration development.
