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
| A1 | Full journey works **through the browser** against the real stack | **Incomplete** — see journey table |
| A2 | A second tenant proves isolation | **Incomplete** — proven at handler level, not through the running application |
| A3 | Unlicensed access rejected by the **API** | **Verified** — an authenticated tenant admin is refused 403 `app_not_licensed` by the *running* tickets service before the grant, and accepted after, on the same session |
| A4 | Fresh-database initialization succeeds using the documented scripts | **Partial** — applied to an empty database on a cold stack; skipped on a warm one, and applied as `postgres` rather than the migrate/runtime roles, so the privilege model is not exercised by this path (D7) |
| A5 | Workflow automated as a CI gate | **Implemented; not yet observed green** — the `e2e` job exists and has run. Run `36143741902` failed on a harness race (D13, fixed); the next push is the first chance to observe it passing |
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
| Create ticket | Partly | Yes | Via `page.evaluate(fetch)` — no UI exists |
| Assign ticket | No | No | Handler + tests only; no UI, not in the journey |
| Close ticket | No | No | Handler + tests only; no UI, not in the journey |

---

## Verified behaviour

Each entry names the test that would fail if the behaviour regressed.

### Tenancy and isolation
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

| Primitive | Why tickets cannot exercise it |
|---|---|
| Monetary invariants | Tickets have no amounts, currency, rounding, or reversal. Balanced double-entry is the central ERP constraint and nothing here touches it. |
| Correction semantics | A ticket is edited freely; a posted journal is reversed, never mutated. Different persistence discipline entirely. |
| Gapless document numbering | Legally required per company per year in many jurisdictions, and contentious under concurrency. Public ids here are deliberately random — the opposite property. |
| Period close and immutability | No concept of a closed period that rejects writes. |
| Aggregate reporting | Trial balance, aging, valuation — read patterns tickets does not have. |
| Multi-company posting | The `company_id` column exists; nothing writes or reads across entities. |

**Consequence.** Infrastructure built against a weak consumer can be subtly wrong for a stronger
one. Two known candidates: the outbox emits one event per aggregate version, which may not fit a
posting that touches many accounts atomically; and row-level tenant filtering may not suit
period- or company-partitioned financial reads.

Neither is a reason to stop. Both are reasons that the next proving app should be a thin
*financial* slice rather than more ticket features — the smallest thing with a ledger invariant,
to test whether these primitives survive contact with money.

---

## Open defects

| ID | Sev | Defect | Reproduction | Consequence |
|---|---|---|---|---|
| **D13** | High — **was failing CI on main** | `startDatabase` used `pg_isready -d appplatform` as its readiness check. `pg_isready` only reports that the server accepts connections; the postgres image runs a temporary init server before creating `POSTGRES_DB`, so readiness passed while the database did not exist. **Fixed** — readiness is now a real `SELECT 1` against the target database. | Reproduced: polling a fresh container showed `pg_isready=yes` while `SELECT 1` still failed, a ~0.4s window. CI run `36143741902` failed with `database "appplatform" does not exist` at `applyMigrations`. | The whole e2e job failed. It passed locally because a cached image wins the race, which is why it reached main — a flake class that only appears on a cold runner. |
| **D12** | Low | `WalkingSkeletonTests` still licenses via raw `INSERT INTO platform.tenant_app`. | `privileges.tests/WalkingSkeletonTests.cs:131` | A handler-level fixture shortcut, not a shipped path. The operator endpoint is covered by the e2e, so this is split coverage rather than a gap — recorded so it is not invisible if the endpoint's behaviour changes. |
| **D10** | High (blocks financial work) | No append-only mechanism. `TenantGuard` permits `Modified`/`Deleted` on any tenant-scoped row, and nothing marks a table immutable. | Inspection: `grep -riE "immutab\|append.only"` finds only a comment on `OutboxEvent` | A posted journal must be reversible, never mutated. Without a central guard, correctness would depend on every future handler remembering. Cheap now, expensive once financial features exist. |
| **D11** | High (blocks financial work) | `packages/audit` does not exist; `IAuditable` appears nowhere in code despite being declared in `CLAUDE.md`. | `ls packages/audit` | "Who changed this and when" is baseline for a ledger. Retrofitting means finding every write path. |
| **D7** | Medium | The e2e applies migrations and connects as the `postgres` superuser, not as the migrate and runtime roles. | `e2e/stack.mjs`, `applyMigrations` | A4's "documented scripts" claim does not exercise the privilege model; a grant regression would pass the browser journey. `AccessMatrixTests` covers grants separately, so this is a gap in what the e2e proves, not an unguarded area. |
| **D8** | Low | Outbox lease is taken per batch but sized for a single send. | `packages/outbox/OutboxBackoff.cs` — `LeaseDuration` 2 min vs `BatchSize` 20 | With more than one worker replica and a slow transport, the tail of a batch can outlive its lease and be re-delivered. Cannot bite today: one replica, instant local transport. |
| **D9** | Low | No unit coverage for `accept-invite.tsx` or the new signed-out routing. | `shell.ui/src` | Four render branches and a problem-code map are exercised only through the browser journey. |
| **D6** | Low | `EFAuthService.FindTokenAsync` uses `IgnoreQueryFilters`, so a token lookup is tenant-blind before the scope is entered. | Inspection, `core.api/Services/EFAuthService.cs` | Necessary — acceptance precedes any session — but it means token-hash uniqueness is the only thing preventing a cross-tenant match. Mitigated by a unique index on the hash and 256 bits of entropy. Recorded so it is a considered exception, not an oversight. |
| **D3** | Medium | The shared Data Protection key ring has no automated assertion. | Remove `DataProtection__KeyPath` from `e2e/playwright.config.mjs`; specs fail with 401 but for an unexplained reason | A regression reappears as "signed in but every API call is 401", which took a browser run to diagnose once already. |
| **D4** | Medium | No ticket UI. Create is exercised via `page.evaluate(fetch)`; assign and close are not exercised through the application at all. | `e2e/specs/journey.spec.mjs:53` | A1 unmet. `fetch` from the page proves the API, not that a user can do it. |
| **D5** | Low | Host-based tenant resolution is unimplemented; `EFTenantResolver` returns null unless `Tenant:PublicId` is configured. | `core.api/Services/ITenantResolver.cs` | Multi-tenant sign-in on one deployment does not work. Deliberate: returning null is safer than guessing, and single-tenant pinning covers current needs. |

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
5. **Result so far.** Provision, deliver, accept, sign in are exercised in the browser. Create is
   exercised through `fetch`, not UI. Assign and close are not exercised through the
   application. Licensing still bypasses the operator API (D2).

---

## Previous cycle

**Cycle 1 — invitation delivery. Accepted.** Outbox worker delivers through a transport; the
browser accepts from the delivered message. Independent review raised 4 blocking findings, all
reproduced and resolved; re-review confirmed them and raised 1 further blocking finding (the
per-message guard's test did not exercise the guard), also resolved. 16 worker cases against real
PostgreSQL. Committed as `4de42d9`.

## Current cycle

**Cycle 2 — license through the operator API (D2). Complete, pending independent review.**

Objective: the tickets entitlement is granted by a real operator over HTTP, with a real session
and CSRF, replacing the raw `INSERT INTO platform.tenant_app` in the seed.

Changed: `CreatePlatformUser` command (`dotnet run -- create-platform-user <email>`, password on
stdin — no API and no self-service, because an operator reaches every tenant); `platform.api`
added to the e2e stack; the seed provisions **unlicensed**; an ordered Playwright project chain
(`unit` → `invite` → `license` → `journey`) so the licensing spec can act as an accepted admin.

Evidence — 12 Playwright specs, all against the real stack:
- An authenticated tenant admin is refused **403 `app_not_licensed`** by the running tickets
  service; an operator then grants the entitlement over HTTP; the **same** tenant session is then
  accepted with 200. Entitlement is therefore read per request, and the API — not the shell — is
  what enforces it.
- Granting the same app twice returns 409 rather than creating a second live row.
- A grant without the CSRF token returns 403 `csrf_failed`; an unauthenticated grant is refused.
- The operator and tenant use separate request contexts, so a shared cookie jar cannot mask a
  mistake between two identities that are separate by design.

Note: `platform.api` readiness is checked by **port**, not URL. Every route there is a POST behind
authorization, so a URL probe gets 405, which Playwright rejects as not-ready; inventing a health
endpoint to satisfy the probe would mean a feature with no handler, which the VSA rules exist to
prevent.

Review status: **pending independent review** — touches authorization, identity and entitlements,
which `docs/product-and-investment-principles.md` requires a separate reviewer for.

**CI fix folded into this cycle (D13).** The `e2e` job was failing on `main`. Root cause was the
harness, not the application: readiness waited on `pg_isready`, which does not check that the named
database exists. Also hardened: `dotnet build` runs before Playwright so `dotnet run` inside the
webServer readiness window starts a compiled app rather than restoring three projects, and failing
runs now upload `e2e/test-results/` so a CI-only failure can be diagnosed without re-running.

Next action: independent review of the Cycle 2 diff; then Cycle 3 — assign and close a ticket
through the UI (D4), which is the last journey step not exercised through the application.

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
