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
| A1 | Full journey works **through the browser** against the real stack | **Verified for every step that has a UI** — accept, sign in, create, assign, close. Provision and license have **no operator UI** (D14) and run over real HTTP and handler paths instead. Delivery is a background worker with no UI by nature. Marking this plainly rather than borrowing A6's standard |
| A2 | A second tenant proves isolation | **Verified** — a real second tenant, signed in with its own session, cannot read, close, reassign, or reference the first tenant's tickets and employees through the running core.api and tickets.api that serve both (`tenant-isolation.spec.mjs`). Found and fixed a real cross-tenant leak on the way (D15) |
| A3 | Unlicensed access rejected by the **API** | **Verified** — an authenticated tenant admin is refused 403 `app_not_licensed` by the *running* tickets service before the grant, and accepted after, on the same session |
| A4 | Fresh-database initialization succeeds using the documented scripts | **Verified** — `PrivilegeFixture` builds an empty database from `database/privileges/01`, all three `migrations-all.sql`, then `02-grants`, applied **as `ap_owner`**, and `99-verify` reports zero findings. The browser harness does the same as superuser (D7) |
| A5 | Workflow automated as a CI gate | **Verified** — run `36146705790` passed all three jobs (dotnet, ui, e2e) in 3m24s after the D13 readiness fix |
| A6 | No step depends on undocumented manual database edits or fabricated authentication | **Verified for the browser journey** — provisioning, delivery, acceptance, licensing and ticket creation all run through real code paths with real sessions. One fixture shortcut remains inside a handler-level test (D12) |

---

## Journey status

| Step | Through the browser | Real path | Notes |
|---|---|---|---|
| Provision tenant | No | Handler, real migrations | Seed calls the real handler; no operator UI |
| **Deliver** invitation | n/a | **Yes** | Outbox worker delivers via `FileEmailTransport`; message captured to disk |
| Accept invitation | **Yes** | Yes | Browser reads the token from the **delivered message**, not the database |
| Sign in | **Yes** | Yes | Real cookie + CSRF issuance, browser-verified |
| License tickets | Operator API over HTTP | **Yes** | Real operator session + CSRF; 403 before the grant, 200 after, same tenant session |
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
| Period close and immutability | No concept of a closed period that rejects writes. | Not yet — next ledger cycle |
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
| **D15** | High — **fixed** | The session endpoint showed every tenant the name of whichever tenant was provisioned first. `TenantNameAsync` read `db.Tenants.FirstAsync()` with no id, and the tenant table has no query filter — it is what tenants are scoped *by*. | Tenant B's session on a core.api reports "E2E Ltd". Invisible with one tenant, which is why nothing caught it. | A cross-tenant disclosure on every dashboard load. Now takes the caller's tenant id explicitly; `AuthTests.The_session_names_the_callers_tenant_not_the_first_tenant` and the e2e cover it. `Tenant` is the only entity without the filter, and its other two reads are keyed. |
| **D13** | High — **was failing CI on main** | `startDatabase` used `pg_isready -d appplatform` as its readiness check. `pg_isready` only reports that the server accepts connections; the postgres image runs a temporary init server before creating `POSTGRES_DB`, so readiness passed while the database did not exist. **Fixed** — readiness is now a real `SELECT 1` against the target database. | Reproduced: polling a fresh container showed `pg_isready=yes` while `SELECT 1` still failed, a ~0.4s window. CI run `36143741902` failed with `database "appplatform" does not exist` at `applyMigrations`. | The whole e2e job failed. It passed locally because a cached image wins the race, which is why it reached main — a flake class that only appears on a cold runner. |
| **D12** | Low | `WalkingSkeletonTests` still licenses via raw `INSERT INTO platform.tenant_app`. | `privileges.tests/WalkingSkeletonTests.cs:131` | A handler-level fixture shortcut, not a shipped path. The operator endpoint is covered by the e2e, so this is split coverage rather than a gap — recorded so it is not invisible if the endpoint's behaviour changes. |
| **D14** | Medium | No operator UI. Provisioning a tenant and granting an entitlement are reachable only over HTTP — `platform.console/` exists as an empty directory. | `ls platform.console` | A1 cannot be "the whole journey in a browser" until an operator has one. Deliberate for this milestone: the customer-facing surface was the priority, and the operator paths are exercised over real HTTP with real sessions. |
| **D10** | High — **fixed** (Cycle 6) | There was no append-only mechanism. | — | Built: `IAppendOnly`, refused in code and by grant, the two kept in step by `BoundaryTests`. See `docs/ledger.md`. |
| **D11** | High — **fixed** (Cycle 5) | `packages/audit` did not exist. | — | Built: see Cycle 5 and `docs/audit.md`. Sign-in events are not audited (security log, not data history). |
| **D7** | Low (was Medium) | The Playwright harness still applies migrations and connects as the `postgres` superuser. | `e2e/stack.mjs`, `applyMigrations` | Narrowed: `PrivilegeFixture` now applies the **shipped** scripts as `ap_owner` and every access test connects as a runtime role, so the privilege model IS exercised — just not by the browser harness. A grant regression fails `AccessMatrixTests` and `VerificationTests`. |
| **D8** | Low | Outbox lease is taken per batch but sized for a single send. | `packages/outbox/OutboxBackoff.cs` — `LeaseDuration` 2 min vs `BatchSize` 20 | With more than one worker replica and a slow transport, the tail of a batch can outlive its lease and be re-delivered. Cannot bite today: one replica, instant local transport. |
| **D9** | Low | No unit coverage for `accept-invite.tsx` or the new signed-out routing. | `shell.ui/src` | Four render branches and a problem-code map are exercised only through the browser journey. |
| **D6** | Low | `EFAuthService.FindTokenAsync` uses `IgnoreQueryFilters`, so a token lookup is tenant-blind before the scope is entered. | Inspection, `core.api/Services/EFAuthService.cs` | Necessary — acceptance precedes any session — but it means token-hash uniqueness is the only thing preventing a cross-tenant match. Mitigated by a unique index on the hash and 256 bits of entropy. Recorded so it is a considered exception, not an oversight. |
| **D3** | Medium | The shared Data Protection key ring has no automated assertion. | Remove `DataProtection__KeyPath` from `e2e/playwright.config.mjs`; specs fail with 401 but for an unexplained reason | A regression reappears as "signed in but every API call is 401", which took a browser run to diagnose once already. |
| **D5** | Medium (was Low) | Host-based tenant resolution is unimplemented; `EFTenantResolver` returns null unless `Tenant:PublicId` is configured. | `core.api/Services/ITenantResolver.cs` | Multi-tenant sign-in on one deployment does not work. Deliberate: returning null is safer than guessing. Raised because A2 needed a workaround — the e2e runs a second core.api pinned to tenant B, used for sign-in only — and a real second customer would need the same, or this. |

---

## Deferred

- **Operator console UI** (`platform.console`) — directory exists, no code. Entitlement granting
  is API-only.
- **A real email transport.** The outbox worker is built and delivers through
  `FileEmailTransport`, which is a local capture substitute gated to non-production. A real
  transport (SMTP/HTTP API) does not exist.
- **Resend invitation.** If delivery dead-letters there is no way to reissue without SQL.
- **MFA enforcement** for operators. Schema and evaluator gate exist; enrolment does not. M2.
- **Backup, restore, key recovery.** M2, and gates the first real customer data.
- **Multi-company** features. Column ships; no UI, no consolidation.
- **Webhooks, public API, API keys.** Designed in `docs/integration.md`; the three irreversible
  decisions are built, the surface is not.
- **Idle timeout, notifications bell** — `ActivityPolicy` anticipates both; neither is built.

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

## Current cycle

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

Next action: the next ledger cycle (period close and a UI) or Milestone 2.

---

## Forward plan

Milestones beyond the mission. Ordering rule: each milestone is thin but end-to-end.

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
