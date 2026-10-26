# Quality ledger

The single source of truth for what is implemented, what is verified, what is broken, and what
is deliberately deferred. Forward plan lives at the bottom.

Every completed cycle must also meet the
[human-readable code standard](human-readable-code.md): simple implementation, useful
comments, and an explicit maintainability review, including VSA behavior locality and the CI slice
structure checks. Functional test results alone do not
establish this acceptance criterion.

Cycles follow the [credit-conscious review process](product-and-investment-principles.md#credit-conscious-development-and-review).
Keep a compact handoff here with acceptance criteria, verification evidence, review status,
remaining risks, and the next action. Use focused checks during iteration and required full
checks at acceptance; reserve independent review for the defined high-risk changes.

**Never describe scaffolding as finished.** The four states below are distinct:

| State | Means |
|---|---|
| **Verified** | Exercised at the layer where it can fail, with a named test |
| **Implemented** | Code exists and compiles; not exercised at its real failure layer |
| **Incomplete** | Partially built, or built behind a shortcut that production will not have |
| **Deferred** | Deliberately not built; reason recorded |

---

## Mission

Product selection and speculative investment follow
[the agreed product and investment principles](product-and-investment-principles.md).
This technical journey proves capability, not customer demand; useful experiments may
proceed before commercial validation while lasting costs remain controlled.

Deliver the customer journey end to end:

**provision tenant → deliver and accept admin invitation → sign in → license tickets →
create → assign → close**

and prove tenant isolation and entitlement enforcement through the real application.

### Acceptance criteria

| # | Criterion | State |
|---|---|---|
| A1 | Full journey works **through the browser** against the real stack | **Verified for every step that has a UI** — accept, sign in, create, assign, close; and, since Cycle 18, provision and license through the operator console (`operator-console.spec.mjs`, on a tenant of its own). The journey's own tenant is still seeded through the handler and licensed over the operator API, because the other specs depend on it existing before they run. Delivery is a background worker with no UI by nature. Marking this plainly rather than borrowing A6's standard |
| A2 | A second tenant proves isolation | **Verified** — a real second tenant, signed in with its own session at its own address, cannot read, close, reassign, or reference the first tenant's tickets and employees through the running core.api and tickets.api that serve both (`tenant-isolation.spec.mjs`), and its session carried to the first tenant's address still reaches only its own data. Found and fixed a real cross-tenant leak on the way (D15) |
| A3 | Unlicensed access rejected by the **API** | **Verified** — an authenticated tenant admin is refused 403 `app_not_licensed` by the *running* tickets service before the grant, and accepted after, on the same session |
| A4 | Fresh-database initialization succeeds using the documented scripts | **Verified** — `PrivilegeFixture` builds an empty database from `database/privileges/01`, all three `migrations-all.sql`, then `02-grants`, applied **as `ap_owner`**, and `99-verify` reports zero findings. The browser harness does the same as superuser (D7) |
| A5 | Workflow automated as a CI gate | **Verified** — run `36146705790` passed all three jobs (dotnet, ui, e2e) in 3m24s after the D13 readiness fix |
| A6 | No step depends on undocumented manual database edits or fabricated authentication | **Verified for the browser journey** — provisioning, delivery, acceptance, licensing and ticket creation all run through real code paths with real sessions. One fixture shortcut remains inside a handler-level test (D12) |

---

## Journey status

| Step | Through the browser | Real path | Notes |
|---|---|---|---|
| Provision tenant | **Yes**, in the console spec | Handler, real migrations | The journey's tenant is seeded through the real handler; the console spec provisions another through the console, platform.api and core |
| **Deliver** invitation | n/a | **Yes** | Outbox worker delivers via `FileEmailTransport`; message captured to disk |
| Accept invitation | **Yes** | Yes | Browser reads the token from the **delivered message**, not the database |
| Sign in | **Yes** | Yes | Real cookie + CSRF issuance, browser-verified |
| License tickets | Operator API over HTTP; the console spec licenses through the console | **Yes** | Real operator session + CSRF; 403 before the grant, 200 after, same tenant session |
| Create ticket | **Yes** | Yes | Through the form in `@app-platform/tickets-ui` |
| Assign ticket | **Yes** | Yes | Picked from core's roster; resolved server-side through `core_v1.employee`; snapshot stored |
| Close ticket | **Yes** | Yes | Closed, disappears from the open list, still named when closed are shown |

---

## Verified behaviour

Each entry names the test that would fail if the behaviour regressed.

### Tenancy and isolation
- **Through the running application**: a second tenant's session, against the same core.api and
  tickets.api processes that serve the first, sees neither its tickets nor its employees, gets 404
  closing or reassigning one by id, and cannot assign the other's employee — `tenant-isolation.spec.mjs`.
  Confirmed non-vacuous: removing the filter from `EFTicketService` fails exactly the three
  ticket cases. Each refusal has a positive control — B's own list, own close, own assignment —
  and a refused assignment is checked for persisted state before the control can overwrite it.
- Query filter scopes reads; no tenant yields **no rows, not all rows** — `TenantFilterTests`
- Insert stamping; insert without a tenant context throws — `TenantGuardTests`
- Cross-tenant update/delete rejected, sync and async — `TenantWriteIsolationTests` (real Postgres)
- Write isolation rests on **two** mechanisms; the guard alone does not catch a forged row id
  relabelled with the attacker's own tenant — only the filter EF appends to the UPDATE predicate
  does. Recorded in `CLAUDE.md`.
- Cross-tenant **reference** isolation: a ticket cannot be assigned to another tenant's employee.
  No foreign key is possible (Postgres cannot key to a view), so the filtered lookup is the only
  defence — `WalkingSkeletonTests.A_ticket_cannot_be_assigned_to_another_tenants_employee`.
  Confirmed non-vacuous: adding `IgnoreQueryFilters()` fails exactly that test.

### Schema privileges (separate guarantee from tenant isolation)
- 21-case access matrix as each runtime role — `AccessMatrixTests`
- `99-verify.sql` asserts negatives; 10 break/repair cases prove it reports violations rather
  than passing vacuously — `VerificationTests`
- Published views readable, owning tables not — proven against the **real** migrations
- Core holds INSERT but **not** UPDATE on `platform.tenant`

### Auth
- Session revalidated per request; revocation, deactivation, role change take effect immediately
  — `SessionEvaluatorTests`
- Suspended tenant **authenticates** and is refused by authorization, keeping the data export
  reachable — `PipelineTests`
- CSRF on every unsafe method, enforced centrally; token issued at sign-in and usable
  — `CsrfIssuanceTests`, browser-verified
- Session resolution against the **real** migrations — `ProvisioningTests`; reverting the status
  parser fails it
- Cookie decryptable across services via a shared, persisted Data Protection key ring
  — browser-verified only; no automated assertion (see D3)

### Audit
- Every create/update/delete of an `IAuditable` entity writes a row in the same save, with the
  actor, the public id, and old/new values of changed columns only — `AuditTrailTests` (unit)
- A redacted property records the change but never the value; the real stack stores
  `password_hash: [redacted]` on invite acceptance — `AuditTrailTests`, inspected after e2e
- A save with no actor throws and saves nothing; non-auditable writes (the outbox worker) need
  none — `AuditTrailTests`
- The change and its row share one transaction: refusing the audit INSERT rolls the ticket back
  too — `privileges.tests/AuditTrailTests` (real Postgres, `ap_tickets_rt`)
- Append-only for every runtime role on every `audit_log`, platform's included —
  `AccessMatrixTests`; `99-verify` reports a regression — `VerificationTests` (3 break/repair cases)
- A request cannot re-attribute its writes; an API key is recorded as the key — `CallerTests`
- New tenant data with a public id cannot skip audit silently — `BoundaryTests` via
  `AuditModelAssertions`, proven able to fail in `AuditModelAssertionTests`

### Ledger
- Lines must be two or more, non-zero, and sum to zero, summed in 128 bits so an overflow cannot
  fake a balance — `ledger.api.tests/LedgerTests`
- The database refuses an unbalanced entry written by raw SQL as the ledger's own role —
  `privileges.tests/LedgerTests` (deferred trigger; removing it fails exactly this and the race test)
- Entry numbers stay gapless under 20 concurrent posts of which 5 fail after being numbered —
  `privileges.tests/LedgerTests` (removing the posting transaction fails it)
- A posted entry cannot be edited: code refuses with `AppendOnlyViolationException`, the database
  with `42501` — `privileges.tests/LedgerTests`, `AccessMatrixTests`; `99-verify` reports a regression
- Two racing reversals yield exactly one, and the loser's number is given back — real Postgres
- Another tenant's account is refused by the handler (resolve) and by the database (composite key)
- Every ledger route answers an unlicensed tenant 403 — `ledger.api.tests/EntitlementTests`,
  through the real route table (removing one `RequireApp` fails it)
- Posts are audited against the caller — `privileges.tests/LedgerTests`
- Raw SQL as `ap_ledger_rt` cannot add a line to a posted entry, use another company's account, move
  an account between companies, skip or waste a number, pick a fiscal year, forge a reversal, or
  delete the counter — `privileges.tests/LedgerTests`, one test per rule, each shown to fail when
  its trigger is dropped
- A retried post (three concurrent, one key) yields one entry; a reused key is refused
- Balances whose totals exceed 64 bits are exact

### Provisioning
- Idempotency enforced **inside** core's transaction by a unique `provisioning_key`; two
  concurrent calls create exactly one tenant — `ProvisioningTests`

### Entitlement
- Unlicensed tenant receives 403 `app_not_licensed`; unauthenticated is left to authorization
  — `EntitlementTests`

### Tickets
- Create/assign/close rules, display-name snapshot, terminated assignee refused — `TicketTests`
- Concurrent close yields one success and one stable 409, and exactly one event
  — `TicketConcurrencyTests` (real Postgres)

---

## What the tickets exercise does and does not probe

The purpose of tickets is to force the platform primitives to work against a real consumer rather
than being built in the abstract. It does that well, and it is shape-independent: none of the
primitives below change if the commercial direction turns out to be ERP, vertical SaaS, or
something else. That optionality is the point.

Worth being precise about which primitives it reaches, because the two sets are different.

**Proved by tickets** — exercised because tickets genuinely uses them:
tenant filter, insert stamping and the cross-tenant write guard; cross-tenant *reference*
isolation through a published view; schema privileges and runtime-role negatives; entitlement
enforcement at the API; per-project migrations against one database; transactional outbox and
delivery; session revalidation, CSRF, and a cookie shared across services.

**Not probed by tickets** — an ERP's hardest primitives, which a ticket system cannot reach:

| Primitive | Why tickets cannot exercise it | Ledger (cycle 6) |
|---|---|---|
| Monetary invariants | Tickets have no amounts, currency, rounding, or reversal. Balanced double-entry is the central ERP constraint and nothing here touches it. | **Proved.** Integer minor units; balance enforced by handler and by a deferred trigger that refuses raw SQL too |
| Correction semantics | A ticket is edited freely; a posted journal is reversed, never mutated. Different persistence discipline entirely. | **Proved.** `IAppendOnly` in code and by grant; reversal at most once, under a race |
| Gapless document numbering | Legally required per company per year in many jurisdictions, and contentious under concurrency. Public ids here are deliberately random — the opposite property. | **Proved.** 20 concurrent posts, 5 failing after numbering, yield exactly 1–15 |
| Period close and immutability | No concept of a closed period that rejects writes. | **Proved (cycle 7).** Enforced by a trigger under a row lock, so a close and a post cannot interleave; forward only |
| Aggregate reporting | Trial balance, aging, valuation — read patterns tickets does not have. | **Trial balance only**, summed in SQL, per currency. No performance data |
| Multi-company posting | The `company_id` column exists; nothing writes or reads across entities. | **Partly.** Every account and entry carries a company resolved through `core_v1.company`; an entry cannot span two. Tested with one company per tenant |

**Consequence.** Infrastructure built against a weak consumer can be subtly wrong for a stronger
one. Two known candidates: the outbox emits one event per aggregate version, which may not fit a
posting that touches many accounts atomically; and row-level tenant filtering may not suit
period- or company-partitioned financial reads.

Neither is a reason to stop. Both are reasons that the next proving app should be a thin
*financial* slice rather than more ticket features — the smallest thing with a ledger invariant,
to test whether these primitives survive contact with money.

**What the ledger found about those two candidates.** The outbox fits: the entry is the aggregate,
many lines ride inside it, and it has exactly one version because it is never edited — one event
per post is natural rather than forced. Row-level tenant filtering composed without friction with
company-scoped reads (the trial balance filters by tenant through the query filter and by company
explicitly), but nothing here is large enough to say anything about its cost.

**What the ledger found that was not predicted.** Two primitives needed more than a thin app
suggests: numbering needed raw SQL (an atomic upsert holding a row lock to commit) because EF has no
shape for it, and the balance rule needed a database trigger because a rule spanning rows cannot be
a check constraint. Both are justified in comments where they live; both are the kind of thing an
ERP will need again.

---

## Open defects

| ID | Sev | Defect | Reproduction | Consequence |
|---|---|---|---|---|
| **D18** | Medium — **fixed** (Cycle 12) | Any signed-in user could create an employee: `CreateEmployeeFeature` checked no role, and packages/auth had no role or policy model, so each feature decided for itself. | A member POSTs `/api/core/v1/employees` and gets 200. | Fixed by the permission model (`docs/permissions.md`): creating an employee needs `core.employee.create` (admin, manager). Evidence: `PermissionTests.A_member_cannot_create_employees_D18`; e2e `employee-invite.spec.mjs`, where the invited member is refused 403 `not_permitted` by the running core. |
| **D17** | Medium — **fixed** | An employee invitation could never be accepted: it staged an email with no token and created no `identity.user_token` row, while acceptance requires one. | e2e `employee-invite.spec.mjs` | Fixed: `InvitationToken.Issue` is now the one issuer (provisioning uses it too); the invite commits user, token and message in one save. The review found this completed a **member-to-admin escalation** (the invite checked no role and can grant admin), so inviting is now admin-only (403 `not_permitted`). Evidence: unit tests, and the e2e spec, where an admin invites, the message is delivered, the invitee accepts in the browser and signs in as member, a reused link is refused, and the member's own invite attempt is refused. Both fail with the fix reverted. Codex, two rounds: no blocking findings. |
| **D16** | Low | The `AddPeriodClose` backfill mints `books.public_id` as `bk_` + 25 hex characters of md5, not the pinned format (25 Crockford base32 characters carrying 125 random bits, `docs/integration.md` §2.1). | `database/ledger/20260925211934_AddPeriodClose.sql`, the backfill `INSERT … SELECT` | Hex is a subset of the Crockford alphabet, so the ids parse, but they carry about 100 bits and use only 16 of 32 symbols. Reaches only databases that already posted entries before the migration, and none is deployed. Fix before the first deployment (generate the body properly in SQL) or accept it in writing. |
| **D15** | High — **fixed** | The session endpoint showed every tenant the name of whichever tenant was provisioned first. `TenantNameAsync` read `db.Tenants.FirstAsync()` with no id, and the tenant table has no query filter — it is what tenants are scoped *by*. | Tenant B's session on a core.api reports "E2E Ltd". Invisible with one tenant, which is why nothing caught it. | A cross-tenant disclosure on every dashboard load. Now takes the caller's tenant id explicitly; `AuthTests.The_session_names_the_callers_tenant_not_the_first_tenant` and the e2e cover it. `Tenant` is the only entity without the filter, and its other two reads are keyed. |
| **D13** | High — **was failing CI on main** | `startDatabase` used `pg_isready -d appplatform` as its readiness check. `pg_isready` only reports that the server accepts connections; the postgres image runs a temporary init server before creating `POSTGRES_DB`, so readiness passed while the database did not exist. **Fixed** — readiness is now a real `SELECT 1` against the target database. | Reproduced: polling a fresh container showed `pg_isready=yes` while `SELECT 1` still failed, a ~0.4s window. CI run `36143741902` failed with `database "appplatform" does not exist` at `applyMigrations`. | The whole e2e job failed. It passed locally because a cached image wins the race, which is why it reached main — a flake class that only appears on a cold runner. |
| **D12** | Low | `WalkingSkeletonTests` still licenses via raw `INSERT INTO platform.tenant_app`. | `privileges.tests/WalkingSkeletonTests.cs:131` | A handler-level fixture shortcut, not a shipped path. The operator endpoint is covered by the e2e, so this is split coverage rather than a gap — recorded so it is not invisible if the endpoint's behaviour changes. |
| **D14** | Medium — **fixed** (Cycle 18) | No operator UI. Provisioning a tenant and granting an entitlement are reachable only over HTTP — `platform.console/` exists as an empty directory. | `ls platform.console` | A1 cannot be "the whole journey in a browser" until an operator has one. Fixed: `platform.console` signs in with MFA, creates, licenses, suspends, resumes and retires tenants, and shows the error feed and operator audit. Evidence: `operator-console.spec.mjs` in the browser; console unit tests at 100% line coverage. |
| **D10** | High — **fixed** (Cycle 6) | There was no append-only mechanism. | — | Built: `IAppendOnly`, refused in code and by grant, the two kept in step by `BoundaryTests`. See `docs/ledger.md`. |
| **D11** | High — **fixed** (Cycle 5) | `packages/audit` did not exist. | — | Built: see Cycle 5 and `docs/audit.md`. Sign-in events are not audited (security log, not data history). |
| **D7** | Low (was Medium) | The Playwright harness's APIs connect as the `postgres` superuser. | `e2e/stack.mjs`, `connectionString` | Narrowed twice. `PrivilegeFixture` applies the **shipped** scripts as `ap_owner` and every access test connects as a runtime role. Since Cycle 8 the harness also builds its database with `database/init-local.sh` (roles, schemas, migrations as `ap_owner`, grants), so its ownership and grants match a deployment and the restore drill's `99-verify` is meaningful. What remains: the running APIs bypass grants, so a grant regression is caught by `AccessMatrixTests` and `VerificationTests`, not the browser. Checked by hand on 2026-09-29 while writing `docs/local-development.md`: every service connected as its own runtime role, and the full journey (operator with MFA, provisioning, licensing, invitation, sign-in, a ticket, the ledger, the error feed) worked with no missing grant. |
| **D8** | Low | Outbox lease is taken per batch but sized for a single send. | `packages/outbox/OutboxBackoff.cs` — `LeaseDuration` 2 min vs `BatchSize` 20 | With more than one worker replica and a slow transport, the tail of a batch can outlive its lease and be re-delivered. Cannot bite today: one replica, instant local transport. |
| **D9** | Low | No unit coverage for `accept-invite.tsx` or the new signed-out routing. | `shell.ui/src` | Four render branches and a problem-code map are exercised only through the browser journey. |
| **D6** | Low | `EFAuthService.FindTokenAsync` uses `IgnoreQueryFilters`, so a token lookup is tenant-blind before the scope is entered. | Inspection, `core.api/Services/EFAuthService.cs` | Necessary — acceptance precedes any session — but it means token-hash uniqueness is the only thing preventing a cross-tenant match. Mitigated by a unique index on the hash and 256 bits of entropy. Recorded so it is a considered exception, not an oversight. |
| **D3** | Medium | The shared Data Protection key ring has no automated assertion. | Remove `DataProtection__KeyPath` from `e2e/playwright.config.mjs`; specs fail with 401 but for an unexplained reason | A regression reappears as "signed in but every API call is 401", which took a browser run to diagnose once already. |
| **D5** | Medium — **fixed** (Cycle 19) | Host-based tenant resolution was unimplemented; `EFTenantResolver` returned null unless `Tenant:PublicId` was configured. | `core.api/Services/ITenantResolver.cs` | Multi-tenant sign-in on one deployment did not work, and the e2e ran a second core.api pinned to tenant B. Fixed: each tenant signs in at its own address, a subdomain of `Tenant:BaseUrl`; the pinning setting is gone, and one core.api signs in both e2e tenants. Evidence: `ProvisioningTests` (resolution against the real migrations), `TenantAddressChangeTests`, and `operator-console.spec.mjs`, where a tenant created in the console signs in at its address, then at its new address only. |

---

## Deferred

- **A real email transport.** The outbox worker is built and delivers through
  `FileEmailTransport`, which is a local capture substitute gated to non-production. A real
  transport (SMTP/HTTP API) does not exist.
- **Operator MFA polish (M3):** step-up re-authentication for destructive operator actions
  (docs/auth-and-access.md §8), and web enrolment/recovery UX. Enforcement itself is Cycle 10.
- **Backup and restore, Stage 2; key recovery.** Stage 1 (scripts and an automated drill) is
  Cycle 8. What needs a hosting provider, and key custody (item 14), remain. Gates the first real
  customer data.
- **Multi-company** features. Column ships; no UI, no consolidation.
- **Webhooks, public API, API keys.** Designed in `docs/integration.md`; the three irreversible
  decisions are built, the surface is not.
- **Customers' own domains** and a redirect from a tenant's former address (D5 built the subdomain
  only); the invitation link on the tenant's address, when a real email transport is built.
- **Idle timeout, notifications bell** — `ActivityPolicy` anticipates both; neither is built.
- **Undoing a termination (rehiring).** Termination is built (Cycle 16); reversing it is not, and
  would have to decide whether the old account comes back with its old roles.

---

## Bets on record

Per `docs/product-and-investment-principles.md`. Kept proportionate.

### Bet 1 — a multi-tenant platform with licensable apps

1. **Hypothesis.** None commercially. There is no identified buyer and no validated demand; the
   principles document says so and this record does not upgrade that. What is being tested is
   *technical*: that a small team can run one platform where tenants are isolated by
   construction, apps are licensed per tenant, and a new app inherits tenancy, auth, entitlement
   and delivery rather than reimplementing them.
2. **Experiment.** One complete narrow workflow — provision through close — with tickets as the
   proving app. Tickets is not asserted as the product.
3. **Downside.** Development time; no recurring cash, no external dependencies, no customer
   data. Discardable: no deployment exists and nothing is published.
4. **Evidence sought.** The journey works through the browser against the real stack; a second
   tenant cannot reach the first's data; an unlicensed tenant is refused by the API; a fresh
   database initializes from the documented scripts. All technical. None of it establishes
   commercial demand.
5. **Result so far.** Accept, sign in, create, assign and close are exercised in the browser;
   delivery runs through the real outbox worker. Provisioning and licensing run over real HTTP
   and handler paths because no operator UI exists (D14). The unlicensed tenant is refused by the
   running API, and a fresh database initializes from the shipped scripts. Outstanding: a second
   tenant proving isolation through the running application (A2).

---

## Previous cycle

**Cycle 1 — invitation delivery. Accepted.** Outbox worker delivers through a transport; the
browser accepts from the delivered message. Independent review raised 4 blocking findings, all
reproduced and resolved; re-review confirmed them and raised 1 further blocking finding (the
per-message guard's test did not exercise the guard), also resolved. 16 worker cases against real
PostgreSQL. Committed as `4de42d9`.

## Fixture drift — resolved at the root

`PrivilegeFixture` applied a hand-written `Fixtures/test-migrations.sql` instead of the shipped
migrations. That schema drifted three separate times, and each time the suite passed while the
application was broken:

1. Core's tenant twin omitted `created_at`, so provisioning would have failed on its first INSERT.
2. The fixture seeded a lowercase tenant status while EF writes the enum name, masking a **total
   auth failure** in which every valid session was rejected as retired.
3. `platform.tenant` again had no `created_at`, which is what surfaced while adding entitlement
   race coverage.

A fixture that diverges from the migrations does not merely miss bugs — it certifies them. After
three occurrences the faulty assumption was the hand-written schema itself, so the fixture now
applies `database/*/migrations-all.sql` and the file is deleted.

Switching immediately exposed four things the old fixture had been hiding:

- Published views and SECURITY DEFINER functions were owned by `postgres`, so
  `identity_v1.touch_session` could not UPDATE `identity.session` and `99-verify` reported the
  views as misowned. Migrations now run **as `ap_owner`**, which is why it is a member of every
  migration role.
- `ap_owner` needed `CREATE ON DATABASE`: `CREATE SCHEMA IF NOT EXISTS core_v1` checks
  database-level privilege before noticing the schema exists. Granted via `current_database()`, so
  the script works under both psql and Npgsql.
- `PublishedContractTests` was pinning the fixture's narrower `core_v1.employee` shape, so it
  pinned nothing about what consumers actually see. It now asserts the real eight columns.
- Three `tickets.ticket` columns are NOT NULL with no database default — EF emits none — which the
  hand-written fixture had papered over with `DEFAULT` clauses.

## Previous cycle

**Cycle 2 — license through the operator API. Accepted.** The grant is an operator action over
HTTP; an authenticated tenant admin is refused 403 before it and accepted after, on the same
session. Independent review raised 1 blocking and 10 optional findings; all resolved. Also fixed a
red CI run (D13) and replaced the hand-written test schema with the shipped migrations after it
drifted for the third time.

**Independent review — 1 blocking finding, 10 optional. All resolved.**

Blocking: a comment in `SeedE2E` justified `IgnoreQueryFilters` by naming raw SQL that this diff had
deleted. Corrected.

Optional findings taken, in order of what they changed:

| # | Finding | Resolution |
|---|---|---|
| O6 | A concurrent duplicate grant returned an unhandled 500 naming a database constraint. The preflight read cannot see an insert that has not happened. | Catches the unique violation and returns the same 409. `DatabaseConflict` in `packages/api` is now the one place that recognises a lost race — three call sites needed it. Covered by `EntitlementConcurrencyTests` against real PostgreSQL. |
| O7 | `CreatePlatformUser` had no tests; three of four branches were unexercised, including the security property that a re-run must **not** reset an existing password. | Split into a testable `CreateAsync`. 10 cases, including a re-run with a different password leaving the hash untouched and writing no second audit row, and email normalisation so case cannot create a second operator. |
| O1 | The tenant sign-in helper did not assert its CSRF token, so a core that stopped issuing one would fail the entitlement assertion and blame entitlements. | Asserted, as the operator helper already did. |
| O2 | The duplicate-grant test passed only because a previous test had granted — broken by `-g`, `--repeat-each`, or any future `retries`. | Folded into the sequence it depends on. |
| O3 | Security guards sat inside the project the journey depends on, so a guard regression reported the journey as skipped. | Moved to a `guards` project that depends on `license` and that nothing depends on. |
| O4 | `testMatch` names files individually, so a new spec would match no project and never run — with Playwright reporting success. | `project-coverage.unit.mjs` asserts every spec is claimed by exactly one project. Verified by adding an orphan spec and watching it fail. |
| O5 | `Internal:ApiKey` was passed to every service, and it is what turns core's anonymous provisioning endpoint from a 404 into a live route. | Split into a `platformEnv` that only platform.api receives. |
| O8 | The evidence paragraph claimed "12 specs all against the real stack" — wrong on both counts. | Corrected above and labelled as measured. |
| O9 | `MinimumPasswordLength` duplication unexplained; the stdin claim overstated. | Both narrowed: the duplication is the assembly boundary, and stdin protects the process list but not shell history. |
| O10 | The CI prebuild was not load-bearing because `dotnet run` could still restore. | `--no-restore` on every `dotnet run` in the harness. |

## Previous cycle

**Cycle 3 — assign and close through the UI (D4). Accepted.**

Objective: a real tickets UI, with create, assign and close driven through the interface rather
than `page.evaluate(fetch)` — the last journey step not exercised through the application.

Changed:

- **`packages/web-api`** — the browser API client, extracted so two UIs share one copy. Credentials
  and CSRF attachment are security rules; a second copy would eventually be the one that stops
  sending the token. Carries the 8 tests that were in the shell.
- **`apps/tickets/tickets.ui`** — a workspace package the shell imports, as the architecture
  specifies. One page: list, create, assign, close. Assignment is a pick-list, never free text, so
  a mistyped name dead-ends instead of being recorded.
- **`shell.ui`** — registry loads `@app-platform/tickets-ui`; the placeholder is gone.
- **`SeedE2E`** — creates an employee through the real `CreateEmployeeFeature` handler, because an
  assignee picker with an empty roster cannot exercise assignment.

Evidence:

- **14 Playwright tests**: 9 touch the real stack (7 in 3 spec files plus 2 guards), 5 are
  pure logic. Counted, not recalled — this line has been wrong three times.
  The journey creates through the form, assigns by picking **Ada Lovelace**, then closes, watches
  the ticket leave the open list, and finds it still naming its assignee once closed tickets are
  shown. That last assertion is the display snapshot doing its job.
- **Where the published view is actually read.** The picker is populated from
  `GET /api/core/v1/employees`, which reads core's **own table** — core owns the roster. The
  published view `core_v1.employee` is read **server-side** by `tickets.api` when it resolves the
  assignee. So the cross-service read is still what the assign step proves: a broken or empty view
  yields `assignee_not_found` and the assertion on the rendered name fails. An earlier version of
  this entry described the picker itself as reading the view, which was wrong.
- A second spec asserts a closed ticket's assignee control is **disabled** and its close button
  absent, rather than offered and then refused.
- **7 unit tests** in `tickets.ui`. One of them found a real bug: `refresh()` cleared the error that
  `run()` had just set, so every failed action showed the user **nothing**. The message is now set
  after the reload.
- The app still code-splits: `dist/assets/index-DFB0yG3L.js` 3.36 kB, separate from the 269 kB
  shell bundle, so an unlicensed tenant never fetches it.

**Independent review — 2 blocking findings, 12 optional. All resolved.**

| ID | Finding | Resolution |
|---|---|---|
| B1 | `SeedCaller`'s comment claimed handlers need a principal for audit attribution. Nothing reads it — not the handler, not tenancy, not the outbox, not SaveChanges. Worse, the shape was `Kind = User, UserId = Guid.Empty`, which satisfies `RequireUserId()` and any role check, bypassing the exact guard `Caller` exists to provide. Same class as last cycle's blocking finding, in the same file. | Comment says what is true. Principal is now an **API key with a null UserId**, so a future `RequireUserId()` throws in the seed rather than writing a zero-guid actor. `CompanyId` is 0, since the handler resolves the company itself. |
| B2 | A1 was marked Verified while the journey table twelve lines below said provision is not in the browser and licensing goes over the operator API. It borrowed A6's standard to do it. | A1 now reads "Verified for every step that has a UI", with **D14** recording the absent operator UI as a deliberate deferral. |
| O3 | **Reproduced**: no request-ordering guard, so ticking "show closed" during a mutation's reload left the checkbox on and the closed tickets absent, with nothing reloading until the next click. | A monotonic request id; a superseded response is discarded. |
| O4 | **Reproduced**: a refused create cleared the title unconditionally, so the page said "check the details and try again" with the details gone. Sibling of the `refresh`/`run` bug found last cycle — same root cause, the other caller. | `run` returns success; the form clears only on it. Test demonstrated failing with the old line restored. |
| O5 | CI ran only `vitest` for the new packages, which never type-checks — falsifying the comment explaining why the shell has a build step. | `build` added to both packages and to CI. It immediately caught an unused parameter in a test file that vitest had passed. |
| O6 | `DatabaseConflict` claimed to be "one place" for three call sites while having one caller, with three inline copies still standing and `IsLostRace` dead. | All four migrated; `grep 23505` now finds only the helper. |
| O7 | A partial seed could never recover: the guard covered the tenant only, so a failed employee step left every later run reporting success with no roster — failing later at the picker, which reads as a UI bug. | Each step guards on its own existence. |
| O8 | `AssignTicketFeature` had no closed-ticket check, and the new e2e comment asserted the API refused it. | Server-side check added returning `already_closed`, with two handler tests. The claim is now true. |
| O9 | Unassign was unreachable: the select is held at `""`, so choosing the placeholder fires no change event. | An explicit Unassign button, shown only when somebody is assigned. |
| O10 | Every mutation refetched the whole roster and blanked the list. | Roster loads once in its own effect. |
| O1, O2, O11, O12 | Test count wrong again; the picker's source misdescribed; a redundant workspace glob entry; a stranded duplicate `ToView`, unsorted usings, a tsconfig naming a file that does not exist. | All corrected. |

**Also added** from the review's test-quality note: create and close had no unit coverage at all,
which is why O3 and O4 shipped. `tickets.ui` now has 13 tests including the refused-create,
refused-close, unassign-visibility and roster-source cases.

**Not taken, recorded instead:** `GetEmployeesFeature` returns employee email addresses to any
authenticated user of any role. That is a product decision about who may see a roster, not a defect
in this cycle — logged in `docs/open-questions.md`.

## Previous cycle

**Cycle 4 — a second tenant proves isolation through the running application (A2). Accepted.**

Objective: tenant isolation is proven at handler level; prove it where it can actually fail — a
real tenant B session against the running services, unable to read, modify or reference tenant
A's data.

Changed:

- **`SeedE2E`** provisions a second tenant, "Other Ltd", through the same real handler. No
  employee: its admin creates one over HTTP in the spec.
- **A second core.api (5103)** pinned to tenant B, because sign-in resolves its tenant from
  configuration (D5). Used for **sign-in only**; every other tenant B request goes to the shared
  core.api (5100) and tickets.api (5102), so the filter is proved inside one process serving both.
- **`tenant-isolation.spec.mjs`** — B's invitation delivered and accepted, B licensed by an
  operator over HTTP, then five cases: session and sign-in scoped to the tenant; tickets invisible
  across tenants; close and reassign by a known id return 404 and change nothing; the other
  tenant's employee refused as `assignee_not_found` on create and on assign; rosters disjoint.
- **D15 fixed** — the cross-tenant tenant-name leak the first run found.
- `isStackReady` now requires both tenants, so a database warmed by the old seed is rebuilt
  rather than starting B's core.api pinned to nothing.

Evidence (measured): 19 Playwright tests pass, 5 of them the new isolation cases; 399 .NET tests
pass including 99 against real PostgreSQL; the filter-removal mutation fails exactly the three
ticket cases.

Not in scope: tenant B through a **browser**. The browser seams (cookie, CSRF, gating) are the
journey's; isolation fails at the API, which is where this tests it.

**Independent review (Codex) — 0 blocking findings, 2 optional. Both taken; re-review confirmed.**

| # | Finding | Resolution |
|---|---|---|
| O1 | The refused cross-tenant assignment was never checked for persisted state; the positive control that followed would overwrite a wrongly saved assignment, so a "refuse but save anyway" regression passed. | The ticket is asserted unassigned immediately after the refusal, before the control runs. |
| O2 | No positive control for closing: a close path returning 404 to tenant B for everything would have passed. | B closes a separate ticket of its own and its persisted status is asserted `Closed`. |

With A2 verified, every Milestone 1 acceptance criterion is met at the standard its row states;
A1's operator steps remain HTTP-only (D14).

## Previous cycle

**Cycle 5 — audit (D11). Accepted** — four Codex review rounds, the last with no blocking findings.

Chosen because it is on both forward paths: a thin financial slice needs it first (with D10), and
it is Milestone 2 item 17. The design was already settled in `CLAUDE.md`; written out in
`docs/audit.md` before building.

Changed:

- **`packages/audit`** — `IAuditable` (public id + tenant scope), `[AuditRedacted]`, `AuditActor`
  (user, API key, or named system process), `AuditedDbContext` staging rows before the tenant
  guard so each row is stamped, and `AuditModelAssertions`.
- **`CallerContext`** supplies the actor from the caller, as it supplies the tenant; `UseActor` for
  pre-session paths, refused inside a request.
- **Pre-session paths declare their actor**: provisioning (`System("provisioning")`), invite
  acceptance and sign-in (the user), the e2e seed (`System("e2e-seed")`).
- `Company`, `Employee`, `User`, `Ticket` are auditable; `User.PasswordHash` is redacted.
- Migrations `AddAuditLog` for core and tickets — additive, one table and two indexes each —
  with per-migration and regenerated bootstrap scripts.
- `02-grants.sql` revokes UPDATE (table and column), DELETE and TRUNCATE on every `audit_log` for
  every `ap_%_rt` role, both found by name, so a new app is covered once `02` runs after its first
  migration; `99-verify.sql` check 12 reports a regression, column grants included.

Evidence (measured, after all review rounds): 454 .NET tests pass, 121 of them against real
PostgreSQL; 19/19 Playwright. Mutations: disabling redaction and the append-only check fails exactly the three
tests covering them; reverting to snapshot "old" values fails exactly the three detached-write
tests; removing the withdrawal fails exactly the retry test. The real stack's audit rows after the
journey were inspected: provisioning, seed, user and ticket writes each attributed as designed.

**Independent review (Codex) — 3 blocking, 4 optional. All taken.**

| # | Finding | Resolution |
|---|---|---|
| B1 | A detached `Update()` sets originals equal to the supplied values, so the snapshot saw no change: the write went unaudited, even with no actor. A detached delete recorded the caller's values as history. | "Old" values are read from the database by key. Tests for detached update, delete, and no-actor. **Found while fixing:** EF's read is not tenant-filtered, so a forged write read the other tenant's row into a staged row before failing — now refused by an explicit tenant comparison, proved against real Postgres. |
| B2 | Rows staged for a failed save stayed tracked; a corrected retry committed a row for the failed change too. | Rows are built all-or-nothing and withdrawn if the save fails. |
| B3 | `GRANT UPDATE (changes)` is invisible to `has_table_privilege` and survives a table-level REVOKE: verify reported clean while history was rewritable. | Verify uses `has_any_column_privilege`; `02` revokes column UPDATE. Break/repair case, plus a test that re-running `02` actually removes it. |
| O4 | Owned-type changes escape audit. | Refused by `FindUnsupportedAuditableShapes`. |
| O5 | Database-generated values and temporary keys recorded as placeholders. | Generated non-key values refused by the same check; temporary values refused at runtime. |
| O6 | The boundary check passed a plain context that mapped the audit table by hand. | Checks the context's type; negative fixture added. |
| O7 | "Covered the day it is created" overstated; the role list was fixed. | Roles found by pattern in both scripts; claim corrected to "once `02` runs after its first migration". |

**Re-review (Codex) — all seven fixes confirmed; 3 further blocking, 1 optional. All taken.**

| # | Finding | Resolution |
|---|---|---|
| R1 | The database "before" was read outside the write's transaction and unlocked: a concurrent writer could change the row in between, so history recorded the wrong "before", and a detached write could overwrite a concurrent change with no row at all. | The row is locked (`FOR UPDATE`, tenant-scoped) and read inside the save's transaction — its own when the caller has none. Real-Postgres test: a concurrent UPDATE fired between the audit read and the write times out with `55P03`, and the row records the true "before". Removing the transaction fails exactly that test. |
| R2 | A detached delete filed the row under the caller-supplied public id. | The stored public id is used; changing one is refused. |
| R3 | `02-grants.sql` ran statement by statement, so its bulk re-grant left the audit logs writable until the revoke. | The file is one transaction. |
| R-O | A row deleted between load and save now surfaced as a 500, not the 409 handlers give a concurrency conflict. | Raised as `DbUpdateConcurrencyException`, as is a forged write — the same outcome `TenantWriteIsolationTests` documents without audit. |

**Third review (Codex) — round-two fixes confirmed; 1 blocking, 2 optional. All taken.**

| # | Finding | Resolution |
|---|---|---|
| T1 | In its own transaction, EF accepted changes before the commit: a failed commit rolled the database back while the tracker believed the work saved, so a retry on the same context wrote nothing. | Changes are accepted only after the commit. Real-Postgres test, sync and async, injects a failed commit and retries the same context: the change lands with exactly one audit row. Reverting fails both. |
| T2 | Rows were locked in tracking order, so two saves touching the same rows in opposite orders could deadlock. | Locked in table-then-key order. No test: a deadlock test would be timing-dependent. |
| T3 | The lock's tenant came from the entity, so a tenant-2 context attaching a row with its genuine tenant-1 value locked and read it before the guard refused. | The tenant guard runs before any lock. Real-Postgres test counts `FOR UPDATE` statements: none. Reverting fails it. |

Not covered by a test: the runtime refusal of a temporary foreign-key value (no current entity can
produce one), lock ordering (T2), and that no other session observes `02-grants.sql` mid-way —
that rests on PostgreSQL's transactional grants, not on anything here.

Decisions taken without asking, each reversible:

- One `audit_log` per **service**, not per schema — core's covers `identity`. `CLAUDE.md` said "per
  schema"; a second log for `identity` would split one account's history across two tables.
- `platform.audit_log` made append-only by the same grant; platform code only ever inserts.
- Audit values include personal data. Retention and erasure are an open question, not decided.

**Fourth review (Codex): all three third-round fixes confirmed, no new findings. NO BLOCKING FINDINGS.**

## Previous cycle

**Cycle 6 — the thin financial slice (D10 and the ledger app). Accepted** — four Codex review rounds,
the last with no blocking findings.

Chosen by Chris over Milestone 2. Scope and deferrals are in `docs/ledger.md`, written before
building.

Changed:

- **D10** — `IAppendOnly` and `AppendOnlyGuard` in packages/tenancy, run by `TenantedDbContext` and
  first in `AuditedDbContext`. `AuditEntry` now uses it instead of its own check. `02-grants.sql` and
  `99-verify.sql` generalise the audit_log rule to listed tables; `AppendOnlyGrantTests` keeps the
  lists in step with the code.
- **apps/ledger/ledger.api** — accounts, post, reverse, list entries, trial balance. Schema
  `ledger`, role `ap_ledger_rt` (its schema, `core_v1`, the session functions; nothing else),
  entitlement `ledger`, boundary registry entry. Migrations `InitialLedger` and `AddBalanceTrigger`
  with generated scripts.
- **No UI and no browser journey** — deferred with period close; the shell registry therefore has no
  ledger entry, and licensing it shows nothing in the nav yet.

Evidence (measured, after all review rounds): 541 .NET tests pass, 156 of them against real PostgreSQL;
19/19 Playwright (unchanged — the ledger is not in the browser stack). Mutations: dropping each
database trigger in turn, the posting transaction, one `RequireApp`, or the 128-bit sum each fails
exactly the tests meant to catch it.

**Independent review (Codex) — 9 blocking, 3 optional. All taken.**

The central finding was that the database enforced *balance* but not the rest of what "posted"
means, so a client holding the runtime role could still change the books without going through a
handler. Each rule is now enforced at the database too — `docs/ledger.md` has the table.

| # | Finding | Resolution |
|---|---|---|
| B1 | A balanced pair of lines could be added to an entry already posted, changing the books with no new entry. | Sealed: a deferred trigger requires a line and its entry to share an inserting transaction. |
| B2 | Keys carried the tenant but not the company: an entry could use another company's accounts, and an account could be moved to another company after posting. | `(tenant, company, id)` keys on account and entry; lines carry company. |
| B3 | Numbering and fiscal year were posting conventions: raw SQL could skip numbers, leave issued numbers unused, or pick a year. | Counter triggers (advance by one; every issued number used; no unissued number), counter undeletable by grant, fiscal-year check constraint. |
| B4 | A retried post created a second entry. | Required idempotency key, unique per tenant, with a request fingerprint; concurrent retries return one entry. |
| B5 | `long.MinValue` passed every check and made the entry unreversible. | Refused by handler and check constraint; reversal negation is `checked`. |
| B6 | Balances and totals were `long` and could overflow. | Summed as `numeric`, returned as `decimal`; test with totals of 2 × `long.MaxValue`. |
| B7 | Raw SQL could post a "reversal" that reversed nothing and took the original's only slot. | Deferred trigger: exact multiset negation, same currency, not earlier, not a reversal of a reversal. |
| B8 | The cross-tenant test could pass with the account key broken — a random entry id failed first. | Valid entry; asserts the named account constraint. |
| B9 | Accounts and balances were read in two statements; a concurrent create-and-post broke the lookup. | One query. |
| O10 | The consistency test used a hard-coded model list and matched names in comments. | Discovers every registered service through its design-time factory; parses only the executed list; requires an exact match. |
| O11 | The overflow example wrapped to −4, not 0, so it could not fail. | `[MaxValue, MaxValue, 2]`; reverting to a 64-bit sum fails it. |
| O12 | "ISO currency" was not checked against ISO. | Described honestly as ISO *form*; supported currencies logged as an open question. |

**Re-review (Codex) — ten fixes confirmed; 2 blocking, 1 optional. All taken.**

| # | Finding | Resolution |
|---|---|---|
| R1 | The reversal-mirror check ran only when the reversal row was inserted. `SET CONSTRAINTS … IMMEDIATE` could run it early, after which a balanced pair appended to the reversal escaped it. | Queued again by every line inserted into a reversal or into an entry that has one. Test forces the check early and appends; dropping the line trigger fails it. |
| R2 | The seal compared `xmin`, which wraps around (freezing keeps the old value, so an old entry can match a new transaction) and wrongly refused a legitimate post split across a savepoint. | Replaced by a declared line count: numbers in 1..count, unique, exact at commit. No transaction ids involved. Tests: a late line inside and outside the range, a savepoint-split post accepted, an entry short of its count, an entry with no lines. |
| R-O | The consistency test stripped only `--` comments inside the list, and a service with no factory contributed nothing silently. | All comments stripped from the whole script first, with tests for both comment forms; every registered service must contribute a model. |

**Third review (Codex) — round-two fixes confirmed; 1 blocking. Taken.**

| # | Finding | Resolution |
|---|---|---|
| T1 | The range trigger skipped lines whose entry did not exist yet, leaving it to the foreign key — but a data-modifying CTE can insert lines before their entry, and the key is checked only at statement end. Lines 3 and 4 of a two-line entry then left slots 1 and 2 free for a later, unbalancing line. | A line whose entry does not yet exist is refused. Test reproduces the CTE; restoring the skip fails it. |

**Also simplified:** with the seal, the per-line completeness trigger was redundant — once the entry's
check passes there is no slot left — and dropping it failed no test, so it was removed rather than
kept untested. The entry-level check is the one that catches an entry with no lines at all.

Found while building:

- A PL/pgSQL `CASE` resolves every field it names, so the first trigger failed on journal entries,
  which have no `entry_id`. Rewritten as `IF`.
- A deferred trigger fails at COMMIT, so the error surfaces as a raw `PostgresException`, not EF's
  `DbUpdateException`. A handler bug that posted an unbalanced entry would therefore be a 500 — the
  right outcome for a backstop, and why the handler checks first.
- `Enum.TryParse` accepts `"asset,liability"` (= Liability) and `"4"`; account types are matched by
  exact name.

Decisions taken without asking, each reversible:

- Any signed-in user of a licensed tenant may post and reverse. Logged in open questions.
- An entry is posted on creation — no drafts. A reversal cannot itself be reversed; re-post instead.
- A reversal cannot be dated before the entry it reverses.
- Posting requires an idempotency key (the review's B4 — a retry must not post twice).

**Fourth review (Codex): the fix confirmed; CTEs, INSERT … SELECT, multi-row VALUES, COPY and
ON CONFLICT checked for further bypasses — none. NO BLOCKING FINDINGS.**

## Previous cycle

**Cycle 7 — the ledger as a proof of concept.** Chris: "use the ledger as a proof of concept", and
pull the latest tonight — so the cycle ships in stages, each reviewed, committed and pushed on its own.

**Stage A — period close. Complete, committed (`b193664`), independent review CLOSED.** Committed
ahead of review while Codex was at its usage limit; the review then ran and its fixes landed in
`399dc46` (see *Review closure* below).

- `ledger.books`, one row per company, created with its first account; journal entries key to it.
- `POST /periods/close` (admin, date before today, forward only) and `GET /periods`.
- Database: entries dated on or before the close are refused by a trigger holding a share lock on
  the books row; closing cannot move back or be deleted.
- Evidence: 162 real-PostgreSQL tests pass; removing the lock fails both interleave tests (post
  first, close first); removing each trigger fails its tests.

**Stage B — a UI and a browser journey. Complete, committed (`ecbd6e6`), independent review CLOSED.**

- `apps/ledger/ledger.ui`, registered in the shell (its own 11 kB chunk, fetched only when licensed).
- The browser journey: license → accounts → post → trial balance → reverse → close → a post into the
  closed period refused, and the page checked at 390px.
- Found: at 390px the page was 438px wide — the tables widened the document. Each table now scrolls
  inside its own container; the journey asserts the width.
- Evidence (measured): 23 ledger-UI tests (money parsing, the line rule, the balance gate, the
  idempotency key on retry — shown to fail when the key is regenerated per click — reversal
  offers, closed-period message); 20/20 Playwright.

**Review closure (`399dc46`, 2026-09-28).** Chris confirmed both stage reviews closed. Fixes:

- `AddPeriodClose` backfills one `ledger.books` row per `(tenant_id, company_id)` that already has
  journal entries, before adding the foreign key, which would otherwise fail on any database where
  Cycle 6 posting had run. **The migration was edited in place.** That is safe only because no
  database SQL has been deployed anywhere yet. Once a script reaches a live database, fixes go in a
  new migration.
- `toMinor` parses through BigInt and refuses amounts above `Number.MAX_SAFE_INTEGER` minor units
  rather than silently rounding them by a cent (test: *refuses an amount too large to represent
  exactly*).
- `database/init-local.sh` added for local database initialisation.

Open from the closure: D16.

## Current cycle

**Cycle 19 — tenants sign in at their own address (D5).** Chris: "lets work on D5 now", after the
console made the gap plain: a tenant created there could not be signed in to, because core resolved
sign-in's tenant only from a pinned setting. **Built, tested, independently reviewed (2026-10-25),
review fixes applied, Playwright 35/35 after them (2026-10-26).**

Decided (Chris, 2026-10-25):
- **A subdomain of one base domain** (`acme-fire.app.example.com`). Customers' own domains are
  deferred (docs/open-questions.md).
- **The operator chooses it, and can change it later.** Suggested from the name at creation.
- **The pinning setting is removed.** The host is the only way sign-in finds a tenant.

Decided by default (stated here, not asked):
- **An address belongs to one tenant forever.** `platform.tenant_subdomain` keeps every address a
  tenant has had; a former one signs nobody in and is never given to another tenant, because people
  still signed in there hold that host's cookie. A tenant may take back its own former address.
  Existing tenants were given `t-<public id>` by the migration.
- ~~An unknown address is 404 `tenant_not_found`~~ — **reversed by the review**; see Review below.
- **Changing an address asks once, without typing the name**: nothing is taken away, and the dialog
  says the old address stops working at once for anyone signed out.
- Rules for an address: 3 to 40 lowercase letters, digits and single hyphens, a letter first; a
  short reserved list (`www`, `api`, `console`, `login` and others).
- One setting, `Tenant:BaseUrl`, for core (the base domain) and platform (the full address shown).

Built:
- `packages/tenancy/TenantAddress`: the rules, parsing a host, building an address.
- **platform**: `tenant_subdomain` (primary key on the address; one current address per tenant by a
  partial unique index; restricted delete) and its migration with the backfill; `subdomain` on
  tenant creation, passed to core, with `subdomain_taken` from core reported as a conflict; `POST
  /tenants/{id}/address` (two saves in one transaction, retiring the old address first; audited as
  `tenant.address_changed`); addresses and the full sign-in address in the tenant reads.
- **core**: the resolver reads the host under the base domain; provisioning writes the first address
  in the tenant's transaction and refuses one already held; sign-in answers `tenant_not_found`.
  `Tenant:BaseUrl` is required at startup in both services.
- **Grants**: core gets SELECT and INSERT on `platform.tenant_subdomain`, nothing more. Recorded in
  docs/database-privileges.md, `99-verify.sql` (core may not modify it, and may read no other
  platform table), `CoreBoundaryTests` (the pinned list of platform tables) and `TwinParityTests`.
- **Console**: the address in the tenant list, the New tenant dialog (suggested, shown in full),
  and on a tenant's page with its former addresses and Change address. **Shell**: the sign-in page
  says when no organization signs in at the address. Its dev proxy keeps the Host for core.
- **Local and e2e**: `bin/dev` passes `Tenant__BaseUrl`, takes an optional address on `tenant create`
  and no longer restarts core pinned; the e2e suite reaches each tenant at `e2e.localhost` and
  `other.localhost`, and the second core.api remains only for the rate-limit spec.

Not built, and recorded:
- **The invitation does not say which address to use.** The accept link is built by whoever sends
  it (bin/dev prints the tenant's address); a real email transport must build it on the tenant's
  current address. An invitation outstanding across a rename carries the old one.
- Customers' own domains; a redirect from a former address.

Evidence:
- .NET 957 passing (was 879). Unit tests for the address rules, creation and the rename handler.
  Real PostgreSQL: resolution, a former address refused, an address held refused with no second
  tenant left, four concurrent provisionings for one address (one wins), concurrent renames of one
  tenant (one current address survives; **with the partial unique index removed the test fails, 3
  of 3 runs**), two tenants racing for one address, an address never given out again, and the reads
  as `ap_platform_rt`. Access matrix and two new 99-verify break-and-repair cases.
- Console: 60 Vitest tests, 100% line coverage. Shell: 67.
- Playwright 35/35. New: a session cookie carried to the other tenant's address still sees only its
  own tenant; the console creates a tenant whose administrator accepts and signs in at its address,
  the operator changes the address, and sign-in then works at the new one and answers
  `tenant_not_found` at the old.
- Local: the migration and grants applied to the dev database as `ap_owner`, 99-verify clean,
  `bin/dev up` running; sign-in through the shell's proxy at the tenant's address reaches it, and
  `localhost:5173` answers `tenant_not_found`.

Review (2026-10-25, `/code-review high` on `8fd1dc5`): 9 findings, none critical. All applied, two by
Chris's decision:
- **Address enumeration (decided: close it).** With wildcard DNS every subdomain reaches us, so 404
  `tenant_not_found` listed customers. An unknown address now answers 403 `invalid_credentials` after
  the same hash work; `tenant_not_found` is gone. The shell's wrong-password message names the address
  so a mistyped one can be spotted.
- **Address history (decided: keep every span).** Taking a former address back overwrote its row's
  dates. New append-only `platform.tenant_address_period` (platform only; core's grants unchanged),
  written by each rename, backfilled from existing former rows; migration `AddTenantAddressPeriods`,
  both SQL scripts generated, listed in `02-grants`/`99-verify`. The console shows each span.
- A retried create with no or an invalid address was refused before the replay; the replay is now first.
- Every core 400/409 refusal reaches the operator with core's code and message (was: only
  `subdomain_taken`, all else "try again"); one shared "address taken" wording (`TenantAddress.TakenMessage`).
- `Tenant:BaseUrl` with a user name or password is refused at startup.
- The console shows the full address only once what is typed has an address's shape.
- Handlers depend on `ITenantSite`, not the concrete record.
- `bin/dev invite-link` builds the link on the invited user's own tenant's address, not the newest tenant's.

Evidence after the fixes: .NET 969 passing (was 957), including real PostgreSQL tests that an address
left, taken back and left again keeps both spans, and that the platform role cannot edit or delete a
span (42501); console 66 tests at 100% lines; shell 66 (two sign-in tests merged into one);
Playwright 35/35, with `operator-console.spec.mjs` expecting 403 `invalid_credentials` at a former
address. The migration is not yet applied to the local dev database.

Found on the way: the suite's fixed ports collided with another local project's servers (5200, 5273,
5274). The restore drill's API answered for a stranger on 5200, because its readiness check accepts
any HTTP answer, and failed with a misleading 404. The suite now serves the shell and console on
5373/5374 and the restore drill on 5300-5304, named once in `e2e/stack.mjs`; the drill refuses a
port already in use before starting.

## Previous cycle

**Cycle 18 — the operator console (D14; M3 item 23).** Chris: "lets work on the operator console
now", after asking what was next. `platform.console` was an empty directory, so creating a customer
and licensing an app needed hand-made API calls. **Built and tested. Independent review (Codex, two
rounds) CLOSED: no blocking findings.** It was required: the change touches operator authentication
(a new sign-out that revokes sessions, and new operator reads). Chris asked for it (2026-10-24).

Built:
- **platform.api**, five slices: `GET /auth/session`, `POST /auth/sign-out` (revokes the session
  server-side and audits `operator.signed_out`), `GET /tenants` (status and live apps, one query),
  `GET /tenants/{id}` (license history and the operator actions on that tenant), `GET /audit`.
- **Creating a tenant is now audited** (`tenant.created`, naming the operator). It was the one
  operator action that left no trail. The entry commits in the same save as the idempotency record,
  so a retried creation is recorded once, and a lost race discards its entry with its record. The
  admin's address is left out: it is customer data.
- **platform.console**: sign-in (password and code together), tenants list and New tenant (one
  idempotency key per opening of the dialog, reused on retry), a tenant's page (suspend and retire
  confirmed by typing the name, with what the customer's people will experience; resume; license
  and revoke; license history; operator actions), Errors, Audit log. "Read at" and Refresh on every
  page; a 401 anywhere returns to sign-in with the reason. Same kit and theme as the shell.
- `bin/dev up` starts the console on 5174. The e2e harness gives the main core.api the internal
  provisioning key, so the console can create a tenant end to end as in a deployment.

Defaults taken (no decision asked for):
- A top bar with three sections rather than the shell's sidebar.
- Licensing an app takes one click; revoking asks first. Resuming asks without a typed name, since
  nothing is taken away.
- MFA enrolment and recovery stay CLI-only, as platform.api/CLAUDE.md requires.
- Local only: the shell and the console share the `ap_csrf` cookie on `localhost`, so signing out of
  one makes the other refuse changes until it is signed in again. Documented in
  docs/local-development.md; production hostnames are separate.

Evidence:
- .NET 879 passing (was 867): platform unit tests for every new handler and the creation audit;
  privileges.tests `OperatorConsoleReadTests` run the new queries as `ap_platform_rt` on real
  PostgreSQL, including a race in which two retries record one creation and exactly one audit entry
  survives.
- Console: 38 Vitest tests, 100% line coverage, enforced by the test run.
- Playwright 34/34, including `operator-console.spec.mjs`: sign in with a code, create a tenant
  through core, license Tickets, suspend by typing the name, resume, find each action in the audit
  log, no horizontal scroll at 390px, and a copied cookie refused as `session_revoked` after
  sign-out.
- All workspaces build (`tsc -b` and `vite build`).

Independent review (Codex):
- **Round 1: 2 blocking, 2 optional. All taken.** The API side was confirmed: every new endpoint
  needs an MFA-complete operator session, sign-out is CSRF-protected and a copied cookie is then
  refused, nothing reads outside the platform schema, and the creation audit excludes the admin
  address. The findings were all in the console:
  - BLOCKING: a failed sign-out still said "You have signed out", while the session stayed valid
    (refresh restored access; a copied cookie kept working). It now returns to sign-in only once the
    server confirms, or a 401 says the session had already ended; otherwise it says the session may
    still be active and offers Sign out again. `csrf_failed` has its own wording.
  - BLOCKING: the New tenant dialog's close button worked while a create was pending, so reopening it
    made a new idempotency key and the same tenant could be created twice. Every close path is now
    refused while busy, and closing re-reads the list, since a failed attempt may still have created
    the tenant.
  - Optional: reads finishing out of order could put stale data on screen with the newest "Read at".
    `useLoad` now shows only the newest read.
  - Optional: the audit page said "every operator action" but shows the latest 100. Reworded.
- **Round 2: fixes confirmed; no blocking findings; 1 optional. Taken.** A reload left over from a
  change on tenant A, finishing after the operator opened tenant B, could show A's data on B's page.
  `useLoad` now ignores reloads for a path the page has left.
- Each fix has a test; the two `useLoad` tests fail with their guard removed. Console after review:
  44 tests, 100% line coverage. Playwright not rerun after the review fixes (the local stack held its
  ports); the fixes change only the console's failure paths, which the unit tests cover.

## Previous cycle

**Cycle 17 — departments, job titles and locations.** Chris: "lets work on departments, job titles
and locations now". Core's CLAUDE.md described the catalogs; none existed, and an employee had a name,
an email and a status only. **Built and tested. No independent review: Chris chose to skip it
for this cycle (2026-10-22) although it adds a per-tenant lock, two migrations and published views.
Evidence instead: real-Postgres race tests that fail when the lock is removed (3 of 3 runs), .NET 867,
shell 65, Playwright 33/33, 99-verify clean on the local database.**

Decided (Chris, 2026-10-22):
- **Departments nest** (Chris chose nested over flat): one parent each, at most 8 levels; no loops;
  an active department never sits under an inactive one, so every active department's whole path is
  in use. Names are unique among siblings ignoring case ("Support" may sit under Sales and under
  Engineering); pickers show the path ("Engineering › Platform").
- **Administrators only** manage the lists: new permission `core.catalog.manage` (Administrator).
  Managers still place people, which is part of `core.employee.update`.
- **A location is a name only.** An address can be added later as further columns.

Decided by default (stated when proposing, not overruled):
- Each employee has at most one department, one job title and one location, all optional.
- Deactivate, never delete. A retired item stays with the people who have it and is not offered to
  anyone else; an edit that keeps it is allowed (EmployeePlacement).
- **No stored copy of the name in core.** Employees hold the id and every read joins, so a rename
  shows everywhere at once and there is nothing to re-stamp. (Core's CLAUDE.md rule about
  re-stamping applies where a display copy sits beside the id; core keeps none. An app that needs the
  name as it was keeps its own snapshot, per the root CLAUDE.md.)
- **No sort order yet**: lists are alphabetical (departments in tree order). The `SortOrder` named in
  core's CLAUDE.md is deferred until someone needs a non-alphabetical list; it is an additive column.
- Job titles are tenant-wide and carry no company; departments and locations carry one (a
  sub-department takes its parent's), as the root CLAUDE.md requires.
- Readable by anyone with `core.employee.read`, since the roster shows them.

Built:
- Data: `core.department` (with `parent_id`), `core.job_title`, `core.location`; the employee's
  `department_id`, `job_title_id`, `location_id`. Every reference carries the tenant (composite
  foreign keys), parent included. Case-insensitive unique name indexes in SQL. Migrations
  `AddCatalogs` (migration role) and `AddCatalogViews` (ap_owner), scripts in `database/core/`.
- Published: `core_v1.department`, `core_v1.job_title`, `core_v1.location`; `core_v1.employee` gains
  `department_id`, `department_name`, `job_title_id`, `job_title_name`, `location_id`,
  `location_name`, appended. No department path in the view: it needs a recursive query the planner
  cannot narrow to the reader's tenant.
- API: `GET/POST /departments`, `PUT /departments/{id}`; the same for `/job-titles` and
  `/locations`. Employee add and edit take `departmentId`, `jobTitleId`, `locationId`; the roster
  returns each as `{ id, name, active }` (a department's name is its path). Events
  `department.created/updated`, `job_title.created/updated`, `location.created/updated`.
- **A new per-tenant catalog lock** (`ICatalogService.LockCatalogsAsync`), taken first by every
  catalog write: the tree rules and the name check read the whole list, which is sound only while
  nothing else changes it. Separate from the role-change lock: catalog edits change no access.
- Shell: Departments (the tree, indented, searchable by path; "Add inside"; edit name, parent and
  status), Job titles and Locations (one shared page), all under Organization in the navigation;
  the employee form gains three pick-only selects, and the roster a Position column.
- Evidence (measured): .NET 867 (was 825): `CatalogTests` (20, unit: names, the tree's rules,
  locking, events), `EmployeePlacementTests` (9), and against real PostgreSQL `CatalogTests` (7: a
  write waits for the catalog lock; two departments moved under each other at once make no loop; two
  same-named job titles at once make one; the database refuses a duplicate name and a cross-tenant
  placement or parent; one tenant never lists another's; an app reads the placement through
  `core_v1.employee` and sees a rename at once), the published-view shapes, and the access matrix
  (apps read the new views; never the tables; the platform reads neither). UI: shell 65 (was 55).
  Playwright 33/33 with the new `organization-structure` spec (build a two-level department, a job
  title and a location; place someone; rename the department and see it on them; 390px fits).
  Broken on purpose, caught each time (3 of 3 runs): the catalog lock removed fails the lock, loop and
  duplicate tests.

## Previous cycle

**Cycle 16 — the employee roster, and inviting people from it.** Chris: "lets work on the employee
roster now. How will we invite users?", then "go with your recommendations". Inviting existed only
as an API (`POST /employees/{id}/invite`); no screen could reach it, and employees could be neither
edited nor terminated. **Complete. Independent review (Codex, two rounds) CLOSED: no blocking
findings.**

Decided (Chris accepted the recommendations, 2026-10-21):
- Every account belongs to an employee. Invitations go from the roster, by employee, never by a
  typed address. The one account without an employee, the administrator made at provisioning, is
  shown as "Not on the roster" and can be given a record (`POST /users/{id}/employee`).
- Editing an employee's email does NOT change the account's sign-in email; the roster shows when
  they differ.
- Terminating an employee deactivates their account under the role-change lock, as deactivation
  does (sessions revoked, last administrator refused, not yourself). Terminated people leave the
  default list and stay visible behind a filter. Undoing a termination is not in this cycle.
- On leave changes nothing about access.
- Departments, job titles and locations: next cycle.
- New permissions `core.employee.update` (Administrator and Manager) and `core.employee.terminate`
  (Administrator only: termination ends access, and a Manager cannot otherwise deactivate anyone).

Built:
- API: `PUT /employees/{id}` (a whole-record replace, so what is saved is what the form showed),
  `POST /employees/{id}/terminate`, `POST /users/{id}/employee`; `GET /users` gains each account's
  `employeeId` (additive). Events `employee.updated`, `employee.terminated`. Ending an account's
  access is one shared helper (`AccountAccess`) used by deactivation and termination; the name and
  email rules are one (`EmployeeRules`) used by adding and editing.
- Shell: an Employees page (search, status, access with "signs in as" when the addresses differ,
  "show terminated", add with "send an invitation" and the role checklist, edit, give access,
  terminate after a confirming dialog); the Users page gains "Invite someone" (picks an employee
  with an address and no account) and "Not on the roster" with "Add to roster". The role checklist
  and the account wording are shared by both pages.
- Fixed on the way: the American-spelling change in Cycle 15 had turned `aria-labelledby` into
  `aria-labeledby` in eight places, silently unnaming every ledger and dashboard section (TypeScript
  does not check hyphenated JSX attributes). Restored; a ledger test now finds each section by its
  name and fails on the misspelling; the CLAUDE.md spelling rule now excludes standard names.
- Evidence (measured): .NET 820 tests (was 798): `EmployeeRosterTests` (18, unit) and, against real
  PostgreSQL, termination ending sessions at once with audit and both events, termination waiting
  for a role change and then refusing the last administrator, another tenant's employee neither
  edited nor terminated, and one account added to the roster twice at once getting one employee.
  UI: shell 54 (was 43), ledger 26, tickets 15, ui-kit 14, web-api 8; every workspace builds.
  Playwright 32/32, with the new `employee-roster` spec: add and invite in the browser, accept the
  delivered link, sign in, terminate (live session 401, sign-in refused, shown behind "show
  terminated" as Terminated and Deactivated), and the provisioned administrator shown as not on the
  roster; 390px fits. Broken on purpose, each caught: termination without the lock; adding to the
  roster without the lock; the section-name misspelling.
- **Independent review (Codex), round 1 — 4 blocking. All taken.**
  1. A Manager could redirect an administrator's invitation: the invite request carried only roles,
     so editing the employee's address while "Give access" was open sent the administrator's chosen
     access to the new address. The invitation now requires the address the inviter was shown,
     compared under the lock; a mismatch is 409 `employee_changed` and nothing is sent.
  2. Editing took no lock, so an edit and a termination could both number their event version 2; the
     outbox's unique index then rolled the termination back, deactivation and session revocation
     included. Editing now takes the role-change lock (real-Postgres test; fails with it removed).
  3. Linking an account to an employee emitted no account event. It now advances the account's
     access version and emits `user.employee_linked`.
  4. Add-to-roster had no cross-tenant test. Added.
- **Round 2: all four fixes confirmed; no new findings. NO BLOCKING FINDINGS.**
- Evidence after the fixes: .NET 825 (5 new), shell 55, Playwright 32/32.

## Previous cycle

**Cycle 15 — the UI kit and a professional shell.** Chris, on first running the platform locally:
"yikes, the ui is really bad", then "do the ui-kit cycle. We need this to look outstandingly
professional." Every page so far was unstyled browser-default HTML: no stylesheet, no Tailwind, no
components, although CLAUDE.md names shadcn/ui and TanStack Table and lists `packages/ui-kit` as
not built. **Complete. Focused self-review; no independent review (no security, data or concurrency
change: sign-out calls the existing endpoint).**

Built:
- `packages/ui-kit` (`@app-platform/ui-kit`): Tailwind v4 theme (colour tokens defined once, light
  and dark), Inter served locally (no font CDN), and shadcn-style components over Radix where a
  primitive earns it: button, field/input/select/checkbox, card, badge, alert, table and a TanStack
  `DataTable` (sorting, filtering), dialog and alert dialog, dropdown menu, page header, empty state.
  Native `<select>` and checkbox stay native (styled): keyboard, mobile and assistive technology
  behaviour for free.
- The shell frame: a sidebar with the organisation, the apps from the registry (which gains an
  icon) and administration; a user menu with the theme choice and **sign out** (the endpoint existed,
  no button did); a mobile top bar with a slide-in menu. Signed-out pages on a centred card.
- Every page moved onto the kit: dashboard, sign-in, accept invitation, users, roles, tickets, ledger.
- Rules kept and enforced: wide content scrolls in its own container (no horizontal page scroll at
  390px, checked in the browser for every page); a dialog states `dismissible` explicitly (a required
  prop) and caps its height in `svh`; accessible names unchanged, so tests keep finding things by
  role and label.
- Decided along the way:
  - Failures inside a dialog are shown in the dialog, which stays open, so the choices made survive.
  - Deactivating a user and deleting a role confirm in an alert dialog instead of a second click.
  - A decorated cell (role chips, a status badge and its detail) gets its words as its accessible
    name (`meta.cellLabel`), because browsers and the test DOM join adjacent elements differently.
  - The theme script is a file (`public/theme-init.js`), not inline, so a future strict CSP allows it.
  - Dependencies pinned exactly, each at least two weeks old (133 packages checked); TanStack Table
    stays on 8.x.
- Evidence (measured): UI suites 43 shell (was 39) / 14 ui-kit (new) / 15 tickets / 26 ledger /
  8 web-api; every workspace builds (`tsc -b`, `vite build`); Playwright 31/31. The browser specs
  changed only where the UI did: nav links scoped to the navigation (a dashboard tile also links to
  each app), the admin's email scoped to `<main>` (the account menu shows it too), the chart of
  accounts read as a table. New tests: non-dismissible dialogs survive Escape, close from their own
  button, cap height in `svh`; the confirm dialog's accessible name; DataTable sorting, search and
  empty state; sign-out and the theme choice; a source scan that every form dialog in the shell and
  the apps passes `dismissible={false}`. Broken on purpose, each caught: the Escape guard removed;
  a form dialog made dismissible (the scan names the file). Every page was screenshotted at 1440px
  and at 390px, light and dark, against fixtures; no horizontal scroll at 390px.
- Not done: `npm audit` reports advisories in Vite 7.3.6 and Vitest 3.2.7 (development tooling only,
  present before this cycle); upgrading Vitest to 4 is its own change.

## Previous cycle

**Cycle 14 — the account lifecycle.** Chris: "start the user lifecycle cycle", after Cycle 13 was
committed (`bd41280`) and pushed. Closes two dead ends an administrator met: no way to deactivate
someone (removing every role still let them sign in), and no way to resend an invitation whose
email failed or whose link expired (only SQL could).
**Complete. Independent review (Codex, two rounds) CLOSED: no blocking findings.**

- `POST /users/{id}/deactivate` and `/reactivate` (`core.user.manage`). Deactivation takes the
  tenant's role-change lock, runs the last-administrator check, refuses your own account
  (`cannot_deactivate_self`), keeps the roles, and **revokes** the sessions as well as refusing them,
  so reactivation never revives a copied cookie. Reactivation restores `Active`, or `Invited` for an
  account that never set a password; refused for a terminated employee. Both audited, with events.
- `POST /users/{id}/invitation` (`core.user.manage`): a fresh link and email in one save, for
  invited accounts only (`not_invited`). Earlier links are left alone; see the next point.
- **Found while designing: accepting an invitation did not check the account's status.** A
  deactivated person holding an unused link could have reactivated themselves, and a spare link
  (an invitation sent twice) could have reset an active account's password. Acceptance now requires
  an **invited** account, and takes the role-change lock before reading it, so a deactivation
  committing at the same moment is seen rather than overwritten.
- `GET /users` gives an invited account's newest invitation: delivery status from the outbox
  (matched by the payload's `kind` with jsonb containment; only status columns are read, never the
  link) and its expiry. Invitation payloads are now built in one place, `InvitationToken`.
- Users page: status in words for invitations, "Resend invitation", "Deactivate" with a second
  confirming click (not shown for yourself), "Reactivate". Fits 390px.
- Evidence before review (measured): 795 .NET tests (204 against real PostgreSQL), 31/31 Playwright, UI suites 39
  shell / 15 tickets / 26 ledger / 8 web-api, all builds clean. New: `UserLifecycleTests` (unit);
  in `UserRolesTests` against PostgreSQL, deactivation waiting for a role change and then refusing
  the last administrator, sessions ending at once and staying dead after reactivation (with audit and
  events), an invitation accepted while the account is being deactivated being refused, and the
  users list reporting a resent invitation while ignoring a later non-invitation email to the same
  address. The real database caught a bug no unit test could: the outbox payload is `jsonb`, so the
  first version's text prefix match failed. New e2e `user-lifecycle`: delivery shown, resend in the
  browser, accept with the new link while the old one is refused, deactivate (live session 401,
  sign-in refused), reactivate (sign-in works, old session still 401). Broken on purpose, each caught:
  acceptance without the lock; acceptance without the status check; deactivation without the lock;
  no session revocation; no `kind` filter; no last-administrator check.
- Decided, worth confirming: you cannot deactivate yourself; a resend leaves earlier links working
  until the account is accepted; who resent an invitation is not recorded (tokens are not audited).
- **Independent review (Codex), round 1: 1 blocking, 2 optional. All taken.**
  - BLOCKING: a sign-in in flight during a deactivation could create a session the deactivation
    never saw. Refused while the account was deactivated, it would have worked again after
    reactivation, breaking the "a copied cookie stays dead" promise. Fixed: sign-in holds the
    tenant's role-change lock in SHARED mode from reading the account until its session commits
    (`IUserService.ShareRoleChangeLockAsync`), so sign-ins never wait for each other, only for an
    access change. Real-PostgreSQL tests both ways: a sign-in waits for a deactivation in progress and
    is refused; a deactivation waits for a sign-in in progress and revokes its session. Removing the
    lock fails the first.
  - OPTIONAL: delivery status was matched to the newest link by address and time, so an older failed
    email (kept 90 days) could be reported for a newer delivered one (pruned after 7), and interleaved
    resends could swap. Fixed: `identity.user_token.message_id` records the email that carried each
    link (migration `AddUserTokenMessageId`, additive, nullable, applied as the migration role; scripts
    generated), and only that message's status is read. Tested against PostgreSQL with the older
    failure dated later, then with the newer email pruned (`unknown`, not `failed`).
  - OPTIONAL: the browser test's "old session stays dead" check reused a context whose cookie the
    first 401 had already deleted, so it proved nothing. Now replays the saved cookie in a fresh
    context and expects `session_revoked`.
  - Evidence after the fixes: 798 .NET tests, 31/31 Playwright.
- **Round 2** (narrow check of the three fixes, including deadlock and starvation with the shared
  lock, rollback of a failed sign-in, tenant scoping of the outbox read, and the migration): NO
  BLOCKING FINDINGS, nothing optional. **Independent review CLOSED.**

## Previous cycle

**Cycle 13 — customer-defined roles.** Chris: "tackle customer defined roles while we're here; it is
an expected feature for any serious ERP type application". Chose: **any number of roles per user**,
a role may span apps, permissions are the union; **built-in roles stay, locked**; customers may define
**core** roles too. Cycle 12 committed first (`6192ca2`). Design rewritten first: `docs/permissions.md`.
**Complete. Independent review (Codex, four rounds) CLOSED: no blocking findings.**

- `packages/auth/Permissions.cs`: `PermissionCatalogue` (every permission with a description, plus
  the new `core.role.manage`), `BuiltInRoles` (keys `app:name`; a colon where permissions use dots,
  which also keeps core's source free of `ledger.`/`tickets.` for the banned-string boundary scan),
  and `Access`: `Granted` (built-in roles plus custom-role permissions, dropping unknown keys,
  unknown permissions and NULL elements), `Licensed` (core plus licensed apps), `Stamp` (a hash of
  what is granted, before licensing). One implementation, used by the session evaluator, sign-in and
  the last-administrator check.
- `identity.role` (custom roles: name unique per tenant ignoring case, by a `lower(name)` index
  created in SQL) and `identity.user.roles` replacing `role` and `app_roles`. Three migrations so the
  database works after each: `AddCustomRoles` (add and backfill), `AddRolesToSessionContext` (as
  `ap_owner`; returns `roles` and `role_permissions`, custom roles joined on the user's own tenant),
  `DropUserRoleColumns`. Dropping in the same release is acceptable only because nothing is
  deployed. Per-migration and full SQL scripts generated; `database/README.md` lists the `ap_owner`
  step and its order.
- core: `PATCH /users/{id}/roles` (`add`/`remove`, so two administrators editing one person both
  get what they asked for, and a role the page does not list is never erased); `GET /roles` (built-in
  and custom, with holder counts); `GET /permissions`; `POST /roles`, `PUT /roles/{id}`,
  `DELETE /roles/{id}` (`core.role.manage`; delete refused while held, `role_in_use`; a role edit
  keeps permissions held in unlicensed apps). Invitations take `roles`, validated by the same rule.
  `Features/Roles/RoleRules.cs` holds the rules every one of these shares.
- The last-administrator rule is now "some active user can still manage users", by permission, so a
  custom role granting `core.user.manage` counts, and editing a role that stops granting it is
  checked too. It refuses only a change from someone to no one.
- **Every role write takes the tenant's role-change lock first**: assigning, creating, editing,
  deleting, and inviting. Cycle 12's admin row lock is gone; with the lock it guarded nothing extra.
- Editing a custom role changes its holders' permissions on their next request, and the access
  claim (now a hash of permissions) signs them in again so their shell matches.
- Shell: session carries `roles` (key and name). Users page: each account's roles by name, and a
  checklist editor that sends only the differences. New Roles page (`core.user.read`): every role,
  what it allows in words, how many hold it; with `core.role.manage`, new, copy, edit, and a
  two-click delete. Both scroll inside their container at 390px.
- Evidence (measured): 757 .NET tests (199 against real PostgreSQL), 30/30 Playwright, UI suites 33
  shell / 15 tickets / 26 ledger / 8 web-api, all builds clean. New: `RoleDefinitionTests`, rewritten
  `ChangeUserRolesTests`, `PermissionTests`, and `UserRolesTests` against PostgreSQL. The last covers
  two admins demoting each other, a second change to one user, a delete racing an in-flight
  assignment, another tenant's user and role refused, `session_context` ignoring another tenant's
  role id even when stored by hand, a custom-role edit reaching holders at once, audit and event, and
  the case-insensitive unique index. In the browser (e2e `employee-invite`), the administrator defines
  a role on the Roles page, gives it to a member on the Users page, and the member can then add
  employees; a member cannot define a role. Broken on purpose, each caught: `session_context`
  without the tenant join; delete without the lock; no licensing filter; unknown permissions not
  dropped; a role edit without the last-administrator check; invitations skipping role validation.
- Decided in the design, worth confirming: holders of `core.user.manage` / `core.role.manage` may
  grant anything, themselves included (no non-escalation rule; open question recorded); a held role
  cannot be deleted; a role needs at least one permission; custom role names may not reuse a
  built-in role's name.
- **Independent review (Codex), round 1 — 1 blocking, 3 optional. All taken.**
  - BLOCKING: the generated SQL scripts were stale. They were generated before the role keys changed
    from `core.admin` to `core:admin`, so an upgraded Cycle 12 administrator would have been
    backfilled with a key that grants nothing, and lost user management. Every test passed because
    the shared fixture seeds AFTER all migrations, leaving nothing to backfill. Fixed: scripts
    regenerated; new `CustomRolesUpgradeTests` builds the Cycle 12 schema from the generated script,
    seeds Cycle 12-shaped users, applies the upgrade, and checks their keys and their permissions
    through `session_context`. It fails with the stale key restored.
  - OPTIONAL: a stored NULL role key threw in `RoleRules.GrantedBy`, blocking that user's sign-in
    and the last-administrator count. Skipped now, and not written back on the next change.
  - OPTIONAL: `[null]` in `add`, `remove`, `roles` or `permissions` was a 500. Now
    `validation_failed` (`RoleRules.CleanList`).
  - OPTIONAL: renaming a role whose permissions are all in an unlicensed app was refused as empty.
    The non-empty check now runs after the kept permissions are merged.
  - Evidence after the fixes: 764 .NET tests (200 against real PostgreSQL), 30/30 Playwright.
- **Round 2 — round-1 fixes confirmed; NO BLOCKING FINDINGS; 2 optional, both taken.**
  - The backfill could make effective what Cycle 12 ignored: a `core:admin` entry inside `app_roles`
    (Cycle 12 set the core role from `role` only), and a second entry for one app (Cycle 12 used the
    first). Now excluded and first-only. The upgrade test seeds both cases.
  - A NULL element in a custom role's stored permissions stopped the role being edited. Skipped.
- **Round 3 — round-2 fixes confirmed; NO BLOCKING FINDINGS; 1 optional, taken:** an entry whose role
  name holds a colon (`ledger:unknown:role`) was Cycle 12's first entry for its app and granted
  nothing, but the backfill filtered it out first and promoted the next one. The filter now splits at
  the first colon, as Cycle 12's decoder did. **Round 4** (narrow confirmation of that fix): confirmed,
  NO BLOCKING FINDINGS; its optional suggestion (seed `""`, `:agent`, `core:` too) taken.
  **Independent review CLOSED.** Final evidence: 765 .NET tests (200 against real PostgreSQL),
  30/30 Playwright, UI suites 33 / 15 / 26 / 8, builds clean.

## Previous cycle

**Cycle 12 — IAM: a permission model (D18).** Chris: "work on IAM and a permissions model";
chose per-app roles fixed in code, and a Users page this cycle. Design written first:
`docs/permissions.md`. **Complete. Independent review (Codex, three rounds) CLOSED: no blocking
findings.**

- `packages/auth/Permissions.cs`: every permission (`<app>.<thing>.<verb>`), the roles per app
  and what each grants. A user holds one core role and at most one role per app. A caller's
  permissions are computed per request from the live row, and only for licensed apps; an unknown
  role grants nothing. Licensing an app grants nobody a role, the administrator included.
- `.RequirePermission(...)` on every customer endpoint, `.AllowAnySignedInUser()` on session and
  sign-out only. `PermissionMiddleware`, wired inside `UseAppPlatformAuth` so no service can omit
  it, answers 403 `not_permitted` (or `app_not_licensed` for a permission in an unlicensed app). An
  authenticated endpoint that declares neither is refused and logged as an error. The two
  hand-written role checks (invite, period close) are gone.
- `identity.user.app_roles` and `access_version` (migration `AddUserAppRoles`), and
  `identity_v1.session_context` returning `app_roles` (`AddAppRolesToSessionContext`, applied as
  `ap_owner`; DROP and CREATE, restoring its grants in the same script). Per-migration and full SQL
  scripts generated; `database/README.md` lists the new `ap_owner` migration.
- The cookie's role claim became an access claim (core role + app roles); any change ends the
  session as `role_changed` so the shell reloads. Security rests on the live recomputation, not
  the claim.
- core: `GET /users`, `GET /roles`, `PUT /users/{id}/roles`. Only the apps named change; granting
  a role in an unlicensed app is refused; the tenant always keeps an active admin (`last_admin`,
  decided under a per-tenant role-change lock); a no-op saves nothing; role changes in a tenant run
  one at a time.
  Audited as an update of the user; emits `user.roles_changed`.
- Session payload adds `appRoles` and `permissions`. Shell: apps gated on a read permission, a
  Users page (saves per select; scrolls inside its container at 390px), a dashboard hint when a
  licensed app has no role for an administrator. Tickets and ledger hide what the role forbids.
  Permission names mirrored in `packages/web-api/src/permissions.ts`.
- Evidence (measured): 716 .NET tests (191 against real PostgreSQL), 30/30 Playwright, UI suites
  24 shell / 15 tickets / 26 ledger / 8 web-api, all builds (`tsc -b`) clean. New: `PermissionTests`
  (matrix, computation, middleware through a host), `PermissionCoverageTests` (every customer
  service's real route table), `PermissionCatalogueTests` (TS mirror; apps match `Apps.All`),
  `ChangeUserRolesTests`, `UserRolesTests` (real Postgres: the last-admin race with one side held
  mid-transaction, the role reaching `session_context` as `ap_tickets_rt`, audit row and event),
  and e2e: the administrator gives themselves a Tickets role on the Users page in the browser, a
  member is refused creating an employee and promoting themselves. Broken on purpose, each
  caught: `FOR UPDATE` removed (race test fails); a permission removed from one endpoint (coverage
  test names it); the middleware's check bypassed (2 tests); a permission removed from the TS
  mirror.
- Behaviour changes worth knowing: closing a period is now a ledger controller's, not a core
  admin's; every e2e spec that works in an app first gives its administrator a role
  (`grantOwnAppRole` in `e2e/stack.mjs`).
- **Independent review (Codex), round 1 — 1 blocking, 2 optional. All taken.**
  - BLOCKING: `access_version` was an EF concurrency token, which EF checks on every update of the
    user. An invitation accepted (or a password rehashed) from a user loaded before a role change
    and saved after matched no row and failed with a 500. Fixed: no token; every role change locks
    the active admins and then the target user (replaced in round 2), so changes to one user run
    in turn and apply on top of each other. The 409 `concurrent_change` path is gone. Tests (real
    PostgreSQL): *a role change does not break another write to the same user* (fails with the
    token restored), *a second change to the same user waits and keeps the first*.
  - OPTIONAL: the 409 and cross-tenant unit tests proved nothing about the database. Replaced by
    real-PostgreSQL tests above and *another tenant's user cannot be changed* (404, row, version and
    outbox untouched).
  - OPTIONAL: a NULL element in `app_roles` would throw during session resolution. `Decode` skips
    it; the malformed-entries test includes one.
- **Round 2 — round-1 fixes confirmed; 1 blocking, 1 optional. Both taken.**
  - BLOCKING: the row locks (admins by id, then the target) could deadlock when a promotion was in
    flight: a waiter's admin query cannot see a member promoted after it started, so two changes
    could take the same rows in different orders, and the loser got a 500. Fixed: every role change
    first takes a per-tenant advisory lock (`LockRoleChangesAsync`, the `KeyRing` pattern), then
    reads the admins (still `FOR UPDATE`, against other writers) and the target. The target-user
    row lock is gone; the tenant lock covers it.
  - OPTIONAL: the same-user test targeted an admin, so the admin lock alone made it pass. Now it
    targets a member while the other side holds only the tenant lock, and checks the second change
    numbered itself after the first. It fails with the tenant lock removed. Not added: a
    three-transaction deadlock test, which could only fail by timing; with one lock per tenant
    there is no second lock to take in the wrong order.
- **Round 3 — round-2 fixes confirmed. NO BLOCKING FINDINGS.** Two optional wording fixes, both
  taken: the `User.AccessVersion` comment now names the tenant lock, and docs/permissions.md §4
  gives the real middleware order (permission before entitlement, which is why the permission check
  itself answers `app_not_licensed`). **Independent review CLOSED.**
- Not done (docs/permissions.md §9): tenant-defined roles, row-level rules, deactivating a user
  from the Users page, operator roles, narrowing roster fields.

## Previous cycle

**Developer tooling: `bin/dev` (2026-09-30).** One command for everything in
`docs/local-development.md`: setup, up/down/status/logs, operator create/reset, tenant create
(licensing, pinning core.api, the invitation link), invite-link, reset. bash 3.2 (macOS).
**Independent review (Codex, four rounds) CLOSED: no blocking findings.** What the review and
testing caught, each fixed and checked:
- stopping a service by `lsof -i :port` also killed its clients (platform.api via core, and the
  browser via the shell); it now matches only the listener;
- secrets in process arguments (the operator password, then the platform key via `env`): bodies go
  to node and curl on stdin, settings are exported in a subshell, and checked with `ps`;
- `.local/` was world-readable: `umask 077` in the script and in `dev.env`;
- a half-finished setup looked complete: a marker is written only after `99-verify.sql` passes;
- a restart could leave the old, unpinned core running: stopping waits for the port;
- `reset` could delete local state while the database survived (failed listings, and the `grep -q`
  SIGPIPE under pipefail, reproduced with a 20,000-line listing): fixed, and a failed listing stops it;
- values written unescaped to `dev.env`, emails not normalised as the API does, the platform key
  exported to every service, and BSD-only `sed -i`.
Verified by full runs on `/bin/bash` 3.2, including a password containing `"` and `\`.

## Previous cycle

**Cycle 11 — the operator error feed (M2 item 17).** Chris: "build the operator feed". Shape as
`platform.api/CLAUDE.md` fixed it: metadata only, bounded, rate-limited per fingerprint, never
blocking, throwing or recursing. **Complete. Independent review (Codex, two rounds) CLOSED: no
blocking findings.**

- `packages/errorfeed`: a logger provider forwarding Error+ entries from anywhere in the process
  (requests, the outbox worker); a fingerprint of the exception's type and top frames, or the
  message TEMPLATE, never the message; a bounded queue (1000, drop when full) with a
  per-fingerprint budget (10/min, the rest counted into the next row); a writer calling the
  platform. A middleware, first in every pipeline, turns an unhandled exception into 500
  `internal_error` with an `err_…` reference, logging the detail locally under it.
- `platform.error_occurrence` (migration `AddErrorFeed`, for the migration role) and
  `platform_v1.record_error` (`AddErrorFeedPublishedFunction`, applied as `ap_owner`; per-migration
  and full SQL scripts): SECURITY DEFINER, every column allowlisted by shape, no foreign key to
  tenant. Customer roles get EXECUTE only;
  no grant on the table, so they can add but not read. Recorded in
  `docs/database-privileges.md` as the second deliberate crossing of the boundary.
- `99-verify.sql` now also reports a published FUNCTION not owned by `ap_owner` (a SECURITY
  DEFINER function owned by a superuser runs as one).
- platform.api: `GET /api/platform/v1/errors` (operators; limit clamped 1–500), hourly pruning after
  30 days. core, tickets, ledger and platform all report.
- Evidence: 15 package tests (fingerprints ignore messages, the budget folds suppressed counts,
  the queue drops rather than blocks, the logger never reads the rendered message or its own
  writer, the middleware returns no exception text); real-Postgres tests (every role can call the
  function, no customer role can read or insert the table, malformed values refused, and the whole
  path from a throwing endpoint in a host connected as `ap_tickets_rt` to a feed row under the
  customer's reference); e2e `error-feed.spec.mjs` (operator reads it, anonymous refused). With the
  function's checks disabled, all five refusal tests fail. 662 .NET tests, 29/29 Playwright.
- **Review.** Round 1: 1 blocking. The migration mixed the table (the migration role's) with the
  published function (`ap_owner`'s), so the documented runbook would have failed: split in two,
  and `database/README.md` now lists every migration applied as `ap_owner`; a runbook test applies
  them with those roles on a fresh database. Should-fix, all done: a reference generated for a
  background error is written to the local log too (a correlation warning); held-back counts are
  kept per tenant; counts saturate at the function's maximum instead of being refused; a full
  budget table evicts finished budgets rather than resetting live ones; the tenant foreign key is
  gone (it let a service probe which tenant ids exist); the new ownership check has isolated tests.
  Round 2: no blocking findings.
- Final: 21 package tests, 673 .NET tests, 29/29 Playwright (the e2e seeds a known occurrence and
  checks that exact row).
- Not done: a console UI (D14); searching by reference is by listing, not a lookup route.

## Previous cycle

**Cycle 10 — envelope encryption and mandatory operator MFA (M2 items 14 and 16).** Chris decided
envelope encryption with a data key per tenant, and deferred where production keys live.
**Complete. Independent review (Codex, three rounds) CLOSED: no blocking findings.**

- `packages/encryption`: `KeyEncryptionKey` (base64 32 bytes from configuration; no default; a
  fingerprint), `DataKey` (append-only, wrapped, keyed by id and KEK fingerprint so rotating the
  KEK adds rows), `KeyRing` (AES-256-GCM, `enc:v1:<key id>:<payload>`, every value bound to its
  row and column; loads at startup, creating the first key; fails there, naming fingerprints, on
  a wrong KEK). Deliberately not yet: the per-tenant lookup and the EF converter. Nothing
  tenant-scoped is encrypted yet, and a converter would capture one key ring in EF's cached model.
- `platform.data_key` (migration `AddDataKeysAndOperatorTotp`, both SQL scripts generated), listed
  append-only in `02-grants.sql` and `99-verify.sql`. `PlatformDbContext` now enforces
  `AppendOnlyGuard` on save.
- `packages/auth/Totp.cs`: RFC 6238, ±1 step, a code accepted once, base32, the provisioning URI.
- Operator sign-in takes password and code together; no half-signed-in session exists. Answers
  are the same `invalid_credentials` until the password is proven, then `mfa_not_enrolled`,
  `mfa_required` or `mfa_code_invalid`. The accepted step is recorded by one conditional UPDATE
  before the session is created.
- `create-platform-user` enrols at creation and prints the secret once. This departs from
  `platform.api/CLAUDE.md`'s "first login walks enrollment": that would let whoever first used the
  password bind their own authenticator. `reset-platform-user-mfa` re-enrols and revokes sessions.
- Evidence: 18 encryption tests; 22 TOTP tests (RFC vectors); 50 platform unit tests, including
  the MFA outcomes; real-Postgres tests for enrolment, reset and the one-code race (8 concurrent,
  exactly one wins); e2e `operator-mfa.spec.mjs` (password alone refused, wrong code refused, a
  used code refused a second time). Broken on purpose, each caught by the right test: MFA checked
  only when a code is sent; the replay guard removed. 27/27 Playwright.
- Harness: operators sign in once per run and share the session (a code works once). The
  readiness check now also requires the operator, after a failed run left tenants but no operator.
- Not done: step-up for destructive operator actions (§8), web enrolment, per-account limiting on
  code guesses (the per-address limit applies).
- **Review.** Round 1: 3 blocking. (a) A reset racing a sign-in could leave a session admitted by
  the OLD secret. Fixed: using the code and creating the session are now one transaction, requiring
  the secret version the code was checked against, and the reset locks the operator row
  (`FOR UPDATE`) before reading sessions. (b) Password-only sessions from before this change
  survived it. Fixed: the migration revokes them (stop the old platform.api before applying). (c)
  Concurrent first starts could each create a first key. Round 2 found my first fix (a fixed first
  key id) still failed when the racers had DIFFERENT KEKs. Fixed properly: a transaction-scoped
  Postgres advisory lock around read-and-create; a wrong-key racer then fails at startup and writes
  nothing. An advisory lock because the runtime role cannot `LOCK TABLE` an append-only table.
  Should-fix: the harness's saved operator session now respects the 30-minute idle and 8-hour
  absolute limits. Round 3: no blocking findings.
- Each fix is proved on real Postgres with the interleaving forced, not hoped for: one side held
  mid-transaction by hand, the other asserted blocked on the lock, then released
  (`OperatorMfaRaceTests`), plus an upgrade test that migrates a database holding a live
  password-only session (`OperatorMfaUpgradeTests`). Each fails when its fix is removed: no version
  predicate, no `FOR UPDATE`, no advisory lock, no revocation step. Final: 630 .NET tests, 27/27
  Playwright.

## Previous cycle

**Cycle 9 — rate limiting anonymous endpoints (M2 item 18).** Chosen as the one unblocked M2 item.
Operator MFA (16) needs `packages/encryption` and the key-custody decision (14) first. **Complete.
Independent review (Codex, two rounds) CLOSED: no blocking findings.**

- `packages/auth/AnonymousRateLimiting.cs`: a global limiter that applies to any endpoint with
  `IAllowAnonymous` metadata. Fixed window per client address, default 10/min, refused with 429
  `rate_limited` and `Retry-After`. IPv6 is grouped by /64 and IPv4-mapped addresses as IPv4.
  `X-Forwarded-For` is believed only from `ForwardedHeaders:TrustedProxies`, one hop deep; with
  none configured it is not processed at all. Production refuses to start without a trusted
  proxy; a limit below 1 is refused at startup.
- Wired into core.api and platform.api ahead of authentication.
- Evidence: 15 host-level tests in `AnonymousRateLimitingTests`. `e2e/specs/rate-limit.spec.mjs`
  runs against a running core.api with a real limit: 7+ ordinary `invalid_credentials` failures,
  then 429. The other e2e servers run at 1000/min, because the suite is one address.
  Broken on purpose, each caught: limiter removed from core.api's pipeline (e2e: 15× 403, no 429);
  loopback trust not cleared; forwarded headers processed with an empty trust list; IPv6 keyed per
  full address. 573 .NET tests, 23/23 Playwright.
- Review round 1 found the blocking case: the framework reads an EMPTY trust list as "trust
  everyone", so outside Production a rotated `X-Forwarded-For` bought unlimited fresh buckets.
  Also the IPv6 /64 grouping, the startup check on the limit, and an e2e assertion that could
  pass vacuously. All fixed; round 2 confirmed.
- Not done: a limit per account (credential stuffing across addresses); uploads (not built yet);
  platform.api's wiring is covered by the shared tests but by no running-server check.

## Previous cycle

**Cycle 8 — backup and restore, Stage 1 (M2 items 13 and 15).** Chris: build Stage 1 now; it does
not depend on a hosting provider. **Complete. Independent review (Codex, three rounds) CLOSED: no
blocking findings.** See *Review* below.

Built:

- `ops/backup.sh`: one `.tar.age` file holding a `pg_dump` (owners and grants kept), a per-table
  row-count manifest, and `about.txt`. The dump and the manifest import **one exported snapshot**,
  so the counts describe exactly the dumped data while the application keeps writing. Encrypted
  with age to a public key, so the backup host cannot read its own backups. Leaves no file that
  looks like a backup if it fails.
- `ops/restore.sh`: refuses a non-empty target, decrypts, creates the roles, restores in one
  transaction, revokes every session, compares row counts against the manifest, and runs
  `99-verify.sql`. Any failure exits non-zero with "Do not use this database".
- `ops/after-restore.sql`: revokes every user and operator session, inside the restore's own
  transaction, so no committed copy ever has working restored sessions. Without it, a session
  revoked after the backup point is live again on the restored copy.
- `Outbox:DeliveryPaused` in core.api: the worker is not registered, and messages stay Pending.
  Without it, the restored outbox re-sends everything already sent after the backup point.
- `database/privileges/00-roles.sql` (idempotent), split out of `01-roles-and-schemas.sql`, which is
  renamed to `01-schemas.sql`. A restore needs the roles without the schemas. Every consumer
  (init-local, e2e, four test fixtures, README) runs 00 first.
- `ops/Dockerfile` (postgres:16-alpine + age). The e2e databases run from it.
- `ops/README.md`: usage, what a backup covers, the steps after the script, what is undecided.

Evidence (measured):

- `e2e/specs/restore.spec.mjs`, the last project in the Playwright run. It backs up the journey's
  database while its APIs run, signs a session out AFTER the backup, restores into a second
  container, and starts core, tickets and ledger against the copy. Checks: the signed-out
  session is refused `session_revoked`; a fresh sign-in reads identical employees, tickets, accounts,
  entries, trial balance and periods; an invitation staged with delivery paused is still
  `Pending` after three poll intervals with nothing captured; a second restore over the now
  populated database is refused. **21/21 Playwright pass.**
- Each safeguard was broken on purpose, and the drill failed each time:
  - revocation skipped → the old session comes back;
  - delivery not paused → the message is `Succeeded`;
  - `pg_restore --no-owner` → 99-verify reports `core_v1.company` and `core_v1.employee` owned by
    `postgres`;
  - a row deleted after the restore → the manifest diff names `core.outbox_message 2 → 1`.
- By hand: restore into a second database on the SAME server (roles already present) passes;
  database-level `CREATE` for `ap_owner` and the `public` schema's permissions match the source.
- .NET: 554 tests pass, including 162 privilege tests, which now apply `00-roles.sql`.

Not covered, deliberately: passwords, deactivations, role changes, accepted invitations, operator
accounts and provisioning after the backup point return to their old state. No script can know
them; `ops/README.md` gives the reconciliation steps. Large objects are refused by both scripts
(the application has none). D16's backfill ids are unaffected.

**Review.** Round 1: 1 blocking, 5 should-fix. Round 2: no blocking, 2 should-fix. Round 3: no
blocking, 3 should-fix, fixed and checked by hand, not re-reviewed (small, local, verified).

- *Blocking, disputed and accepted by the reviewer:* a restore makes a used invitation link valid
  again until its original expiry. It was NOT fixed by expiring tokens. The restore also returns
  the account to Invited with no password, and nothing can reissue an invitation, so expiring
  them would lock the admin out with SQL as the only recovery. Documented in `after-restore.sql`
  and the README, with the hand fix for a known-leaked link. Codex: accepted "for the current
  issuers"; **revisit when password reset or reissue exists.**
- *Fixed:*
  - restore and session revocation in one transaction, with COMMIT sent only if pg_restore
    succeeded, and a `DID NOT FINISH` warning if a changed target fails its checks;
  - a stricter emptiness check (functions, types, extensions, large objects);
  - backup names that cannot collide (per-run partial, hard-link publish);
  - large objects refused at backup, inside the snapshot, and at restore, before the target is
    touched (pg_restore would COMMIT them early);
  - README reconciliation for operators and provisioning;
  - the drill now proves a message restored as Pending is not sent.
- *Checked by breaking each one:* pg_restore cut mid-stream, or failing after full output, leaves
  0 app schemas; a function in `public` is refused; a taken name is refused with no partial file
  left; a large object is refused by both scripts; unpaused delivery fails the new drill assertion
  (`Succeeded`).
- *Found on the way:* D17, employee invitations cannot be accepted (pre-existing).

Final evidence: 21/21 Playwright, 554 .NET tests, shellcheck clean.

Next: commit. Stage 2 needs a hosting provider: recovery targets,
schedule, off-site copies, who holds the age identity (with item 14), and a timed drill against
the managed database, including whether its admin role can assign ownership to `ap_owner`.

---

## Forward plan

Milestones beyond the mission. Ordering rule: each milestone is thin but end-to-end.

## Milestone 2 — safe to hold real data

**Gates the first customer deployment.** Nothing below is optional once someone else's
data is in the database. Expand/contract migrations and an application rollback rehearsal
against the upgraded schema are required here, even with coordinated releases.

13. Backup of database **and** file storage together, restored and **verified working**,
    not merely completed. A restore that has never been exercised is a hypothesis.
14. **Encryption key custody and recovery.** Losing a key-encryption key loses the data under it
    permanently. Where the key lives, who can retrieve it, and how recovery is rehearsed
    must be written down and tested before the key protects anything real. Key
    *rotation* can wait; key *recovery* cannot. *Structure decided and built in Cycle 10
    (envelope, per-tenant data keys, KEKs from configuration); the custody LOCATION is deferred by
    Chris (docs/open-questions.md) and still gates the first deployment.*
15. Restore drill script and runbook. *Stage 1 built in Cycle 8: scripts, an automated drill on
    every CI run, and `ops/README.md`. Stage 2 (provider, timed real drill) outstanding.*
16. **Mandatory operator MFA, enforced.** An operator reaches the control plane for every
    tenant, so an unprotected operator account is a larger exposure than most of this
    milestone. Enforcement belongs to the gate; enrolment polish and the recovery UX can
    wait for M3. *Enforced: Cycle 10.*
17. Per-project audit logging and the operator error feed (metadata only). *Audit: Cycle 5. Error
    feed: Cycle 11.*
18. Rate limiting on anonymous auth endpoints and uploads. *Anonymous endpoints: built (see
    Cycle 9 below and `docs/auth-and-access.md` §3a). Per-account limiting and uploads (not
    built) remain.*

There is an uncomfortable symmetry worth noticing: the September work concluded the
sellable service was a recovery check. Being unable to restore this platform would be
disqualifying.

---

## Milestone 3 — widen

19. Catalogs: departments, job titles, locations, with rename fan-out and pick-only
    controls. *Built in Cycle 17 (nested departments; no stored name copies, so no fan-out is
    needed). Not built: a sort order other than alphabetical.*
20. Terminology overrides and the settings editor.
21. Tenant-configured ticket status and category wording over fixed enums.
22. Comments, attachments, ticket audit trail.
23. Operator MFA enrolment polish and recovery UX; operator audit; tenant lifecycle
    screens. *Built in Cycle 18: the console, its operator audit page and the tenant lifecycle
    screens. Not built: MFA enrolment and recovery in the browser; both stay CLI-only on purpose
    (enrolment at creation, recovery by `reset-platform-user-mfa`).*
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
