# tickets — first licensed app

Owns the `tickets` schema. Root rules in `../../CLAUDE.md` apply.

## What this app is for

**It proves the machinery, not the market.** Ticketing exercises the entire platform —
entitlement gating and the 403 path, the shell registry, shared employee data through
`core_v1`, notifications, attachments, audit, per-app migrations — without a ledger's
invariants. That is its job.

It is also the most commoditized category in software (Zendesk, Freshdesk, Jira, free
Zammad and osTicket). **Do not let it quietly become the revenue bet.** If a decision
here trades platform-proving value for ticketing feature parity, it is the wrong trade.
Recorded in `docs/open-questions.md`.

## Scope for v1

Tickets with requester, assignee, status, priority, category, description, comments,
attachments, and an audit trail. Tenant-configurable status and category wording.

Deliberately **not** in v1: SLAs and escalation, email-to-ticket ingestion, customer
portal, knowledge base, time tracking, automation rules. Each is a real feature; none of
them proves anything about the platform.

## Shared data

- Requester and assignee are `employee_id` read from `core_v1.employee`, **plus a stored
  display snapshot** taken when the reference is made. A closed ticket must still name
  its assignee after that employee is deleted from core. Live join for the current
  state, snapshot for history.
- This app never writes to core. An assignee who does not exist yet is a core problem,
  solved in core.
- Never map a `core` table or another app's schema. `BoundaryTests` will fail.

## Tenant-configured wording

Status and category are tenant wording, not codes — "Waiting on customer", not
`pending_customer`. Two rules carried from EMS's discipline catalogs:

- **Only hand over a list nothing branches on.** If code counts or routes on a value,
  renaming it silently changes behavior. Anything the app reasons about (open vs closed)
  stays a fixed enum with a tenant-facing *label*, never a tenant-editable *value*.
- Tickets **snapshot the name**, so renaming never rewrites history, and editing a
  ticket re-admits its own stored value even if that option was since retired —
  otherwise retiring an option makes every old ticket using it uneditable.
- Empty catalog means "never configured" and defaults are offered. All rows inactive
  means "configured to none" and nothing is offered.
