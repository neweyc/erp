# Architecture

**Status:** working design, v0.1 — September 24, 2026
**Decided in:** conversation of 2026-09-24. Nothing here is validated by a customer.

## 1. Shape

One monorepo, several independently deployed processes over one PostgreSQL database with
a schema per project.

```
                      +-------------------+
   operator  ------>  | platform.console  | ---> platform.api  --> schema: platform
                      +-------------------+                          + tenant (slim)
                                                       |
                                                       | internal provisioning call
                                                       v
   customer  ------>  +-------------------+ ---> core.api      --> schemas: core, identity
                      |     shell.ui      |                        publishes: core_v1.*
                      |  (single SPA)     |                                   ^
                      |  apps mount here  | ---> tickets.api   --> schema: tickets
                      +-------------------+                        reads ----+
```

## 2. Why a monorepo when apps deploy separately

A repo boundary is not a deployment boundary. Pipelines decide what ships together, and
path-filtered CI plus per-project version tags keep app releases independent.

What the monorepo buys: one copy of tenancy, auth, audit, encryption, storage, and the
UI kit — written twice already across redshift and EMS, and a third copy-paste is the
genuinely bad outcome. Also one CI config and no cross-repo PR dance for a solo builder.

What it costs, and what pays for it:

| Cost | Mitigation |
|---|---|
| An app change could republish platform images — an accidental platform release | Path-filtered CI, per-project tags |
| Project references quietly re-couple releases | Shared code ships as versioned packages, never project references across a deploy boundary |
| Reaching across the app boundary is one line away | `BoundaryTests` fails the build |
| Carving out an app for a client or publisher becomes `git filter-repo` | Accepted; revisit if a third party ever ships an app |
| Larger context per session, CLAUDE.md rules bleeding between projects | Per-project CLAUDE.md scoped to its directory |

EMS is the precedent: it holds a platform and a tenant app in one repo over one Postgres,
and it stayed honest only because a boundary test suite polices it. That worked. It is
also why the enforcement here is not optional.

## 3. How apps share data

Each project owns a schema. Core publishes **versioned read views** — `core_v1.employee`,
`core_v1.department`, `core_v1.company`, `core_v1.job_title` — and an app may map its own
schema plus those views and nothing else. No app writes an employee; writes go through
core's API.

### Why views rather than an event bus with per-app replicas

The textbook answer is: core publishes `EmployeeChanged`, each app maintains a local
read-model, apps join locally. That is the right answer *at a scale this is not at*.

With views, joins keep working in SQL. "Open tickets for anyone in Station 3, sorted by
surname, paged" stays one query. Push employees across a queue into a local replica and
that query either N+1s or needs a projection rebuilt — solving a problem that does not
exist yet. Operationally it is also one backup, one restore, one key rotation, one
connection string, run by one person. Given the business thesis is *someone keeps it
working*, that is not a small consideration.

### The escape hatch, which is the actual reason to pick views

If an app ever must run on separate infrastructure, replace `core_v1.employee` with a
locally materialized table fed by events. **The app's query code does not change**,
because it was always reading that name. The event bus is deferred, not foreclosed.

Tenancy composes for free: the views carry `tenant_id`, so a consuming app applies its
ordinary `ITenantScoped` global query filter to them with no new machinery.

### The four kinds of shared data

Conflating these is how the design goes wrong.

1. **Identity & access** — session claims, revalidated per request. Never queried per view.
2. **Org master data** — core owns, apps read `core_v1.*`. The genuinely shared thing.
3. **Cross-app reference** — store the foreign id **plus a display snapshot**. A ticket
   closed in 2024 must still name its assignee after that employee is deleted. Live join
   for current state, snapshot for history. This is the one that gets skipped.
4. **App-to-app workflow** — events. **Deferred** until a customer pays for two apps and
   asks for it. Building it before then is speculation with a maintenance cost.

## 4. Core is a spine, not a product

Core = who works here, in what structure, with what access. Companies, employees,
departments, locations, job titles, identity, roles, settings, terminology, audit.

HR features — training, certifications, compliance, discipline — are an **app**. The
alternative, a required EMS-sized core, forces a customer who wants a ticket system to
buy fire-department compliance tooling. That is a hard sell and it muddles what "core"
means. Thin required spine; everything else lights up on payment.

## 5. Tenant and company

- **Tenant** — the customer. Contract, billing, entitlements, and the security boundary.
- **Company** — a legal entity: own books, fiscal calendar, tax registration, filings.

Every tenant gets exactly one company at provisioning. `company_id` is not-null on core
entities that belong to an entity, defaulted to it. **No switcher, no consolidation, no
multi-company UI in v1.**

The column exists now because the schema is cheap and the data archaeology is not.
Retrofitting means asking a live customer which legal entity each historical employee
belonged to — a question they usually cannot answer. Carrying the dimension makes
multi-company a feature you enable rather than a backfill you survive. This is cheap
insurance, not an averted emergency.

Two rules keep it from metastasizing:

- **Company is an accounting dimension, not an access boundary.** Real SAP makes company
  codes an authorization object; that builds a second tenancy system inside the tenancy
  system and doubles every permission check. Entity A's staff must not see entity B's
  data? That is **two tenants**.
- **Company is not a division.** No separate books and filings means it is a department
  or location, which core already models.

## 6. The shell is a single SPA

One session, one nav, one theme, one terminology provider, instant switching, the
component kit shared at runtime.

This **deliberately merges the UI deploy boundary** — a ticket UI fix redeploys the
shell. Accepted, because the independence that matters is at the API and database layer
where the data and migrations live. Module federation buys runtime independence at the
cost of a category of version-skew bugs that are miserable to debug alone. Revisit only
if a third party ships an app.

App UIs are workspace packages the shell imports, so the **code** boundary survives even
though the deploy boundary does not. The shell talks to each API through that API's
versioned route prefix, so a shell deploy never forces a simultaneous API deploy.

Apps register declaratively in one registry — the front-end analogue of discovering
`IEndpoint` by reflection. Route-level dynamic import keyed on entitlement means
unlicensed app code never reaches the browser.

## 7. Entitlements are the product

EMS designed this mechanism and never built it, because nothing there was ever premium.
Here it is the business model, so it exists from the first commit: `platform.tenant_app`
rows, a `.RequireApp()` filter returning 403 `app_not_licensed`, licensed apps in the
session payload, and a "tenant without the app gets 403" test per feature.

Handlers and services never check entitlements — same discipline as tenancy. Disabling an
app hides functionality, never deletes data. Apps depend only on core, never each other.

## 8. Version independence

- Route prefixes carry a version: `/api/tickets/v1/...`. Breaking change means `v2` with
  `v1` kept for a release.
- **Expand/contract migrations only.** App version N-1 must run against version N's
  schema, or rollback is not possible.
- `compatibility.md` records supported (shell x API) combinations. Independent cadences
  produce a grid; a monorepo makes it look like there is one version of the world.

## 9. What this design does not yet answer

See `open-questions.md`. The largest: whether anyone pays for any of it.
