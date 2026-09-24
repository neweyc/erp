# App Platform

Working name. A multi-tenant business application platform: a thin required **core**
(organization, people, identity), optional **licensed apps** that light up when a
customer pays, and an operator **platform console**.

Successor in spirit to EMS/redshift — it carries their proven patterns and corrects
their known mistakes. Where a rule here contradicts EMS, the difference is deliberate
and the reason is stated inline. Don't "fix" it back.

## The three things in this repo

1. **Platform** (`platform.api` + `platform.console`) — operator surface: tenants,
   provisioning, entitlements, billing records, key rotation, error feed, operator
   audit. **Never reads customer business data.**
2. **Core** (`core.api`) — required for every tenant: companies, employees,
   departments, job titles, locations, identity/roles, settings, terminology, audit.
   Owns the published `core_v1` views that every app reads.
3. **Apps** (`apps/*`) — licensed, optional, one schema each, mounted into one shell.
   First app: **tickets**.

**Core is a spine, not a product.** HR features (training, certifications, compliance,
discipline) are an app like any other — they do NOT belong in core. A customer buying a
ticket system must not be forced to buy fire-department compliance tooling to get
employee records. Anything that is not "who works here, in what structure, with what
access" belongs in an app.

## Repo layout (single git repo, mixed .NET + npm workspaces)

```
app-platform.slnx            .NET solution
package.json                 npm workspaces root (shell.ui, apps/*/ *.ui, packages/*)
platform.api/                operator control plane API      -> schema: platform
platform.console/            operator SPA (own hostname)
core.api/                    org + people + identity         -> schemas: core, identity, core_v1
shell.ui/                    THE customer-facing SPA         (apps mount into it)
apps/
  tickets/
    tickets.api/             licensed app API                -> schema: tickets
    tickets.ui/              workspace package imported by shell.ui
packages/                    shared libraries, versioned (see Shared code)
database/                    generated SQL scripts, seed/reference data, ER diagrams
e2e/                         Playwright smoke of the critical journey
ops/                         backup/restore/drill, deployment scripts
docs/                        architecture, decisions, backlog, open questions
```

Prioritized roadmap lives in `docs/backlog.md`; undecided things live in
`docs/open-questions.md`. Keep both current — an open question that has been answered
in conversation but not written down will be re-litigated.

## Tech stack

| Layer | Choice |
|---|---|
| APIs | ASP.NET Core (.NET 10) Minimal API, Vertical Slice Architecture |
| Data | EF Core + PostgreSQL (one database, schema per project) |
| UI | React + TypeScript + Vite, shadcn/ui, TanStack Table |
| Unit tests | xUnit + Moq (.NET), Vitest + happy-dom + Testing Library (UI) |
| Integration | Testcontainers against real Postgres |
| E2E | Playwright |
| Hosting | Docker Compose, nginx front door, managed PostgreSQL |

## API architecture (all APIs, no exceptions)

- **One feature per file**: `Features/<Area>/<Verb><Entity>Feature.cs` containing three
  nested classes:
  - `<Verb><Entity>Command` — request DTO.
  - `<Verb><Entity>CommandHandler` — primary-constructor DI of **interfaces only**;
    `Handle(Caller caller, cmd)` returns `CommandResult`.
  - `Endpoint : IEndpoint` — Minimal API `MapGet`/`MapPost`, `[FromServices]` injection,
    reads userId from `ClaimTypes.NameIdentifier`, news up the handler manually,
    `.RequireAuthorization(...)` and (in an app) `.RequireApp(...)`.
- **`Caller`, not a user id.** EMS passed `Guid userId` because a user was the only thing
  that could call anything. Here an API key is a first-class principal
  (`docs/integration.md` §2.3), and a signature that can only carry a user id forces every
  machine-authenticated call to invent a fake one — which then lands in audit rows as a
  person who did not do it. `Caller` carries the principal id, its kind (user or api_key),
  the tenant, the role, and the granted scopes; `UserId` is nullable and null for a key.
  **Audit records the principal**, so "deleted by integration key 'payroll-sync'" is
  representable. Settled before the first handler exists, because retrofitting a parameter
  through every feature file is the expensive version.
- Endpoints are **auto-discovered by reflection** (any `IEndpoint`) at startup. No
  manual route registration, ever.
- **Services** live in `Services/`: `IXxxService` interface + `EFXxxService(DbContext)`
  implementation, registered `AddScoped`. Keep them thin EF wrappers. Handlers depend on
  interfaces so they unit-test without a database.
- `CommandResult.CreateIResult` maps handler results to HTTP responses. Failures carry a
  stable `problemCode` string the UI can branch on — never a message match.

### Route namespace and versioning

One shell calls many APIs, and they deploy on independent schedules. Therefore:

- Every API is mounted under its own prefix: `/api/core/v1/...`, `/api/tickets/v1/...`,
  `/api/platform/v1/...`. nginx routes by prefix to the right container; everything is
  **same-origin** so cookie auth works without CORS.
- A **breaking** change to a published route means a new version segment (`v2`), with
  `v1` kept for at least one release. Additive changes stay in place.
- The shell must never require a simultaneous API deploy. If a shell change needs a new
  API field, ship the API first.

## Tenancy, companies, and the access boundary

- **Tenant** = the customer. Contract, billing, entitlements, and the **security**
  boundary. Tenant-scoped entities implement `ITenantScoped { int TenantId }`; the
  DbContext applies a global query filter, auto-stamps `TenantId` on insert, and throws
  on cross-tenant updates. **Handlers and services never set or filter by TenantId.**
- **Company** = a legal entity (own books, fiscal calendar, tax registration). Every
  tenant gets exactly one at provisioning, silently. `company_id` is not-null on core
  entities that belong to an entity (employee, department/location) and defaults to it.
- **There is no multi-company UI, switcher, or consolidation, and v1 builds none.** The
  column exists so that adding multi-company later is a feature you enable rather than a
  backfill that asks a live customer which legal entity each historical employee
  belonged to — a question they usually cannot answer. The schema is cheap; the data
  archaeology is not.
- **Company is an accounting dimension, NOT an access boundary.** Real SAP makes company
  codes an authorization object; that builds a second tenancy system inside the tenancy
  system and doubles every permission check. If a customer needs entity A's staff unable
  to see entity B's data, that is **two tenants**.
- **Company is not a division.** If it is not a separate set of books with separate
  filings, it is a department or location — core already models those. Modelling
  divisions as companies fills the entity table with things that should never be
  entities.

TenantId is also a concurrency token on writable tenant-scoped entities: tracked
UPDATE/DELETE predicates must include the original tenant, including detached writes.
Current and original tenant values must match the scope; tenant reassignment is forbidden.
Real PostgreSQL tests cover forged attached updates/deletes, not only honest tenant values.

### Row isolation is not reference isolation

Query filters stop you *reading* another tenant's rows. They do nothing to stop you
*writing a reference to one*: nothing prevents a tenant B ticket storing tenant A's
`employee_id`, or an employee pointing at another tenant's department. Three defences,
all required:

- **Resolve, never trust.** Every inbound foreign id is loaded through the filtered
  context before use. A `null` is a validation failure, not a missing row. An id that
  arrives in a request body is user input.
- **Composite foreign keys carry the tenant** *within a schema*: `(tenant_id,
  widget_id)` referencing `(tenant_id, id)`. This makes a cross-tenant reference
  impossible at the database level rather than merely unlikely — the one defence that
  survives a bug in the resolve step. `TenantModelAssertions.FindReferencesNotCarryingTenant`
  fails the build on a tenant-blind reference.
- **Cross-schema references cannot have one.** They point at a published view, and
  PostgreSQL cannot key to a view — an app also has no grant on the table behind it. So
  for `tickets.ticket.assignee_employee_id -> core_v1.employee`, resolve-never-trust is
  the *only* write-time defence, backed by a scheduled integrity check that joins on
  `(tenant_id, public_id)` and reports orphans. Treat every cross-schema id as untrusted
  for as long as it exists, not merely on the way in.
- **Cross-tenant reference tests**, alongside the read/write isolation tests: tenant B
  attempting to reference tenant A's employee must fail, not silently persist.

Two paths bypass all of this and must be treated as unsafe by default:

- **Raw SQL** applies no query filter. Justify it in a comment and scope it explicitly.
- **`ExecuteUpdate`/`ExecuteDelete`** skip `SaveChanges` entirely, so they perform no
  tenant stamping, no audit, and no projection rebuild. Prefer tracked saves; where bulk
  is genuinely needed, say in the code why the missing audit row is acceptable.
- **Background jobs have no ambient tenant.** They enter one explicitly per scope. A job
  that forgets **reads nothing and writes nothing** — the filter matches no rows without a
  tenant and an insert throws. That is required behaviour, not an accident of the
  implementation: failing empty is recoverable, failing open is a breach. The symptom is a
  job that silently does nothing, so a job should assert it holds a tenant.

Carried from redshift's QC review, non-negotiable from day one:

- Never use `FindAsync` for tenant-scoped entities (it can bypass query filters via the
  change tracker); use a filtered `FirstOrDefaultAsync`.
- Anonymous/unauthenticated endpoints have no tenant context — any data access there
  must scope explicitly and be reviewed carefully.
- Soft-delete filters and tenant filters must compose; a deleted-row guard must not open
  a cross-tenant hole.
- List endpoints must actually honor their filter query params.

## Data sharing between apps — THE central rule

Each project owns a Postgres **schema** in **one** database. Full rationale and the
escape hatch: `docs/architecture.md`.

- `platform` — operator tables. `core` — org/people. `identity` — users, sessions,
  tokens, MFA. `tickets` — the first app. One schema per app thereafter.
- **Core publishes versioned read views**: `core_v1.employee`, `core_v1.department`,
  `core_v1.company`, `core_v1.job_title`. Thin, denormalized, display-safe, carrying
  `tenant_id` so a consuming app applies its ordinary `ITenantScoped` filter to them
  with no new machinery.
- **An app may map its own schema plus `core_v1`, and nothing else.** Never a `core`
  table. Never another app's schema. `BoundaryTests` scans every EF model and fails
  otherwise — the same enforcement EMS needed to keep its platform boundary honest.
- **No app ever writes an employee.** Writes go through core's API.
- **A view is a contract.** Additive columns are fine. Removing or renaming one means
  `core_v2.*` alongside `core_v1.*` until every app has moved.

### The four kinds of "shared data" — do not conflate them

| Kind | Example | Where it lives |
|---|---|---|
| Identity & access | who is signed in, role, licensed apps | session claims, revalidated per request — never queried per view |
| Org master data | employees, departments, companies | core owns; apps **read** `core_v1.*` |
| Cross-app reference | a ticket's assignee | app stores `employee_id` **plus a display snapshot** |
| App-to-app workflow | closing a ticket bills an hour | events — **deferred** until a customer pays for both apps |

The third is the one that gets skipped. A ticket closed in 2024 must still show who it
was assigned to even if that employee is later deleted from core. **Live join for
current state, stored snapshot for history** — the same rule EMS uses for
`PositionChange.RankOrGrade` recording what the rank was *called* then.

## Entitlements (the business model, built in slice 1)

EMS designed this and never built it; here it is the product, so it exists from the
first commit.

- App constants in `packages/entitlements` (.NET) **mirrored in
  `packages/entitlements-ts`** for the shell. Keep the two in sync.
- Entitlements live in `platform.tenant_app` — rows only for licensed apps. Core has no
  row; it is always on.
- App endpoints declare `.RequireApp(Apps.Tickets)`. The filter returns **403** with
  problem code `app_not_licensed`. **Handlers and services never check entitlements.**
- The session payload carries the tenant's licensed apps; the shell gates nav and routes
  on them, and **route-level dynamic import means unlicensed app code never reaches the
  browser**.
- **Shell gating is not access control.** The shell hides; the API enforces. Every
  licensed-app feature needs a "tenant without the app gets 403" test.
- Disabling an app hides functionality, never deletes data. Apps depend only on core,
  never on each other.

## Auth & identity

- Cookie auth, **server-side sessions**: every cookie carries an `identity.session` row
  id, revalidated on each request, so revocation, deactivation, role change, and
  entitlement change take effect immediately rather than at cookie expiry.
- APIs return **401** (not 302) for unauthenticated `/api` requests; the shell handles
  redirect-to-login client-side.
- **Revalidation calls one published function**, `identity_v1.session_context(session_id)`,
  which joins session, user, tenant status, and licensed apps and returns at most one row.
  An app therefore observes revocation, role change, suspension, and entitlement change
  immediately **without** being granted the `identity` or `platform` schemas — which
  resolves the otherwise-contradiction of "apps may not read platform tables" against
  "entitlement changes take effect at once".
- **A function, not a view like `core_v1`.** A grant on a view is a grant to read all of
  it, and a session id is credential-equivalent — `SELECT *` would enumerate every live
  session in every tenant. Views for joining and paging; functions for a keyed lookup that
  must not enumerate.
- **EXECUTE is granted to customer API roles only, never the platform role.** Operator
  sessions live in platform tables; letting the platform resolve tenant sessions would
  undo its isolation.
- **"Only `packages/auth` calls it" is a code convention, not a grant.** Any code on that
  connection can. The grant is the boundary against other *services*; `BoundaryTests` is
  the boundary against other *code in the same service*. Neither covers the other's case.
- **Authenticate first, then authorize.** A suspended tenant's session still establishes a
  principal — rejecting at authentication would discard the identity the promised data
  export needs. Suspension is **403** with an allowlist and the cookie intact; retirement
  revokes sessions and is an ordinary 401.
- **Failure is specific, not a generic 401** — `session_revoked`, `role_changed`,
  `tenant_suspended`, `tenant_retired`, `app_not_licensed`, `mfa_required`.
- **Operator and customer cookies are isolated** by name and host scope; a console
  session can never authenticate a tenant API, or the reverse.
- **CSRF**: cookie auth requires it. `SameSite=Lax` plus a double-submit header token on
  every **unsafe method** — POST, PUT, PATCH, DELETE. The test is safe-versus-unsafe,
  never idempotent-versus-not: PUT and DELETE are idempotent and change state, so a rule
  written around idempotency leaves most of the write surface open. Key-authenticated
  calls need no token — a browser does not attach a bearer key on its own.
- Full design, including what tenant context is established from and how background jobs
  enter one: `docs/auth-and-access.md`. **Write that before building auth**, not after.
- MFA (TOTP) optional per user, mandatory for platform operators. Tenant-required MFA is
  enforced at **sign-in**, never mid-session.
- Permissions defined once in `packages/auth` (`Roles.*` arrays + `Policy*` constants),
  **mirrored in the shell's `permissions.ts`** — keep in sync whenever either changes.
- `identity.user.employee_id` links an account to a core employee. It is what makes
  termination cut access, and it gates all self-service.

## The shell (single SPA)

- **One SPA** serves every app: one session, one nav, one theme, one terminology
  provider, instant switching, the component kit shared at runtime.
- **This deliberately merges the UI deploy boundary** — a ticket UI fix redeploys the
  shell. Accepted: the independence that matters is at the API and database layer, where
  the data and migrations live. Module federation buys runtime independence at the cost
  of a category of version-skew bugs that are miserable to debug solo. Revisit only if a
  third party ever ships an app.
- App UIs live in `apps/<app>/<app>.ui` as **workspace packages** the shell imports —
  code boundary preserved even though the deploy boundary is not.
- **Apps register declaratively.** One registry entry — id, name, icon, routes, required
  entitlement, required roles, terminology keys — and the app appears in nav, router,
  and permissions. Adding an app must never mean edits scattered through the shell. This
  is the front-end analogue of `IEndpoint` reflection discovery.
- Wide content scrolls inside its own container, never widening the document. `min-w-0`
  on the shell content wrapper and on every DataTable root is load-bearing and easy to
  delete by accident. Check `document.documentElement.scrollWidth === innerWidth` at
  390px when adding a page.
- Dialogs holding a part-filled form pass `dismissible={false}` (a backdrop click
  otherwise discards entry with no undo) and cap height in `svh`, never `vh`.

## Migrations & database

- **Each project owns its own schema's migrations**, with its own history table
  (`MigrationsHistoryTable("__ef_migrations_history", "<schema>")`). This is a deliberate
  break from EMS, where one project owned every migration including the platform's —
  that only worked because nothing deployed independently.
- Entity mapping in `OnModelCreating`; **snake_case** tables and columns.
- Workflow: change the model -> `dotnet ef migrations add <Name>` -> review the generated
  migration -> generate SQL. **Never auto-migrate on startup.** Chris applies SQL to live
  databases by hand.
- After each migration generate BOTH scripts into `database/<schema>/`: the per-migration
  one (`--idempotent`) and the full bootstrap used to init fresh databases.
- **Expand/contract only, from the first customer deployment (Milestone 2).** Even
  coordinated releases must be rollable back, so
  version N-1 of an app has to run against version N's schema. No destructive migration
  ships in the same release as the code that stops using the column.

## Cross-cutting concerns (shared packages, not copy-paste)

You have now written these twice (redshift -> EMS). A third copy is the bad outcome.

- `packages/tenancy` — `ITenantScoped`, query filters, stamping, cross-tenant guard.
- `packages/auth` — cookie scheme, session revalidation, MFA, roles, policies.
- `packages/audit` — `IAuditable`; create/update/delete rows written automatically in
  `SaveChangesAsync`. **Each schema owns its own `audit_log` table.** Entity CRUD
  handlers never call audit methods; only non-entity events (login, password change) are
  audited manually.
- `packages/encryption` — AES-256-GCM field converter. Encrypted columns get no max
  length and **cannot be searched or filtered in SQL** — keep queryable fields plaintext.
- `packages/storage` — `IFileStore`; bytes at `tenant-{id}/{app}/{yyyy}/{MM}/{guid}`,
  metadata in the app's own `stored_file` table. **Never touch the filesystem from
  feature code.** Downloads always serve `Content-Disposition: attachment` + `nosniff`.
- `packages/email` — `IEmailService`, transport selected by which credential is present.
  **Sending is never in the request transaction** — see Reliability below.
- `packages/outbox` — the transactional outbox table and its worker.
- `packages/ui-kit` — shadcn components, DataTable, dialogs, form primitives.
- Shared packages are **versioned dependencies, not project references across deploy
  boundaries.** A project reference re-couples the releases this layout exists to
  decouple.
- **That rule activates at the first independent app release.** While the supported
  matrix is still "everything from the same commit" (see `docs/compatibility.md`),
  project references are fine and the packaging ceremony is premature. Do not let
  publishing infrastructure block the first working end-to-end journey.

## Reliability: outbox, not send-then-commit

EMS's rule was "stage the writes, send the email, then commit", so a failed send left no
orphaned invite. **That trade is wrong and this repo does not carry it.** It swaps a
visible failure for an invisible one: if the send succeeds and the commit then fails, the
recipient holds a link to an invitation that does not exist, and a retry after timeout
can provision twice.

- **Commit the record and an `outbox` row in one transaction.** A database-backed worker
  sends with retries and marks the row delivered. No event bus, no broker — one table and
  one hosted service.
- Delivery is **at-least-once**, so anything an email triggers must be idempotent.
- **Every externally-triggered provisioning call carries an idempotency key** with a
  unique index. A retry returns the original result instead of creating a second tenant.
- The outbox relocates EMS's original problem rather than deleting it: a permanently
  failing send now leaves a committed invite nobody received. So **delivery status is
  visible on the record**, resend is idempotent, and an undelivered invite must never
  block re-inviting that address. Design that in from the start; it is the whole reason
  the old rule existed.

## The key boundary (what the platform actually cannot do)

Three mechanisms, each guarding a different thing. Stating the guarantee precisely
matters, because the imprecise version — "the platform cannot read customer data" — is
false and would be believed.

| Mechanism | What it actually guarantees |
|---|---|
| Platform never receives `Encryption:FieldKey` | **Encrypted columns only** — PII, narratives, termination reasons — are unreadable ciphertext. It says nothing about plaintext. |
| `PlatformDbContext` maps only `platform` + slim `tenant` | Code **in this repo** cannot accidentally query customer tables. It does not constrain raw SQL or a compromised process. |
| **Postgres role grants** (`database/privileges.sql`) | The connection is *unable* to read `core`, `identity`, or any app schema. This is the only one of the three that holds against code that is not in this repo. |

- Employee names, emails, ticket titles, and department names are **plaintext**. Without
  role grants, a platform process connected to the same database can read all of it. The
  encryption key protects encrypted fields and nothing else — never describe it as
  protecting "customer data".
- **Every project runs as its own Postgres role**, granted only its own schema plus the
  published views it is allowed to read (`core_v1.*`, `identity_v1.session_context`).
  That is what makes the schema whitelist a boundary rather than a coding convention —
  and it closes the same hole for apps, where `tickets.api` could otherwise read
  `core.employee` directly and bypass the published view.
- **Runtime and migration credentials are separate.** The runtime role has no DDL. See
  `docs/database-privileges.md`.
- **Key rotation**: the console *initiates and tracks* a rotation; the re-encryption job
  runs inside the core/app process that holds the key. The console never sees the key or
  plaintext.
- Operator actions land in `platform.audit_log`, separate from tenant audit by design.
- Provisioning **delegates to core.api** via an internal endpoint behind a shared secret
  (unset = 404, checked before the body is read; nginx blocks the internal prefix
  publicly). One transaction: tenant + company + admin invite, with the invite email
  staged in the outbox and sent after commit — carrying an idempotency key, so a retried
  call returns the original tenant rather than creating a second.

## Integration surface (customers integrating with us)

Full design and the deferral boundary: `docs/integration.md`. Three rules bind now.

- **Every externally-referenceable entity carries a stable opaque public id** (`emp_...`,
  `tkt_...`) in its own column, separate from the primary key. Sequential integers leak
  row counts across tenants and cannot be re-keyed. The internal key never leaves the
  database. This is genuinely irreversible once a customer stores our ids.
- **Every domain write emits an event into the outbox**, from the first feature, even
  with no subscribers. Adding emission later yields no history, so the first integrating
  customer meets a system with amnesia. A row per write is cheap; the missing past is not.
- **An API key is its own principal** — own table, own scopes, own audit, own revocation
  — never a user row flagged as a service account. Machine identities modelled as users
  end up in employee lists and inherit a permission model built for humans. Two
  authentication schemes, one authorization model: every policy check asks what the
  **principal** may do.

The public API is a **curated** surface at `/api/public/v1/`, mapped from internal
features and deliberately narrow — the same published-contract discipline as `core_v1`
views and route versions, at the outer edge where it is least forgiving. Internal routes
are never quietly promoted; the moment a route is public its shape is frozen. Webhooks
reuse `packages/outbox` rather than introducing a second delivery mechanism: at-least-once,
signed, **thin payloads carrying no PII**, with SSRF defences on every customer-supplied
URL including the cloud metadata address.

## Testing

- `*.tests/` — xUnit + Moq, one file per feature area, mock the service interface, no
  Docker. Fast.
- `*.integration.tests/` — Testcontainers against real Postgres. This is where real-SQL
  behavior lives: tenant filter/stamping/cross-tenant guard, `core_v1` view contracts,
  concurrency races, paging determinism. InMemory's stable sorts and non-transactional
  saves hide real bugs.
- **Every tenant-scoped feature needs a "tenant B cannot see/modify tenant A's data"
  test.** Every licensed-app feature needs an "unlicensed tenant gets 403" test.
- `BoundaryTests` (its own project) enforces the schema whitelist, the identity-mapping
  rule, and the no-cross-app-reference rule. These are architecture, not style — they
  fail the build.
- UI: Vitest. Type-check with `npm run build` (`tsc -b`), NOT bare `tsc --noEmit` — the
  latter skips test files and a type error there fails the Docker build on main after a
  green local check.
- `e2e/` — Playwright smoke of one sequential critical journey against its own isolated
  stack. Deep per-feature coverage belongs in unit tests.

## CI & deployment

- **Path-filtered CI.** A change under `apps/tickets/` must not rebuild or republish
  platform images — that would turn an app change into an accidental platform release.
- **Per-project version tags** (`tickets/v1.4.2`, `platform/v0.9.0`), not repo-wide
  semver. Each deployable has its own version and changelog.
- Image publication **`needs:` every test job**, including boundary tests. A separate
  `on: push` workflow would race the tests instead of waiting for them — exactly how a
  red suite once shipped an image in EMS.
- `docs/compatibility.md` records the supported (shell x API) version combinations.
  Independent cadences produce a grid; a monorepo makes it look like there is one version
  of the world, and there is not.
- Production sits behind TLS; the auth cookie is `Secure`. nginx `client_max_body_size`
  tracks the upload cap, `proxy_request_buffering off` keeps large uploads streaming.
- Back up the database and the file storage volume **together**.

## Working agreements

- Chris applies SQL to live databases manually — write or generate scripts, never execute
  against a live DB.
- Commit only when asked.
- All substantive work gets a codex review, looped to mutual satisfaction, before commit.
- Keep `docs/backlog.md` and `docs/open-questions.md` current as features ship and
  decisions land.
