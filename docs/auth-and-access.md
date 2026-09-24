# Auth and access revalidation

**Status:** design, v0.1 — must be settled before `packages/auth` is built.

Every API must observe revocation, deactivation, role change, tenant suspension, and
entitlement change **immediately**, not at cookie expiry. Those facts live in `identity`
and `platform` — both outside an app's schema whitelist. This document resolves that.

## 1. The contradiction, and the fix

The architecture says apps may read only their own schema plus `core_v1`. It also says
entitlement changes take effect at once. Taken literally those conflict: an app cannot
check `platform.tenant_app` per request.

**Fix: one more published contract** — but a *function*, not a view.
`identity_v1.session_context(p_session_id uuid)` joins session, user, tenant, and
entitlements and returns **at most one row, for a session id the caller already holds**.

A view would have been the obvious parallel to `core_v1`, and it is wrong here. A grant on
a view is a grant to read all of it, so `SELECT * FROM identity_v1.session_context` would
enumerate every live session in every tenant — and a session id is credential-equivalent.
Views are for joining, filtering, and paging; a keyed lookup that must not enumerate is a
function. `docs/database-privileges.md` carries the SECURITY DEFINER hygiene this
requires.

```
identity_v1.session_context(p_session_id uuid) returns
  session_id        uuid
  user_id           uuid
  tenant_id         int
  company_id        int
  tenant_status     text      -- active | suspended | retired
  role              text
  licensed_apps     text[]
  employee_id       uuid null -- the self-service link
  mfa_satisfied     bool
  revoked_at        timestamptz null
  last_seen_at      timestamptz
  absolute_expiry   timestamptz
```

Always fresh because it is a join, not a copy. One indexed read per request; cache within
the request scope, never across.

- Created and owned by `ap_owner`, because it reads across `identity` and `platform` and
  core's migration role can see neither of those together. Deliberate, and the only
  cross-schema reach in the system.
- **EXECUTE granted to customer API roles only** — `ap_core_rt`, `ap_<app>_rt`. **Never
  the platform role**: operator sessions live in their own platform tables, and letting
  the platform resolve tenant sessions would undo the isolation the rest of this design
  pays for.
- **"Only `packages/auth` calls it" is a code convention, not a grant.** Any code on a
  connection holding the EXECUTE privilege can call it; `BoundaryTests` is what keeps
  other code from doing so. Stating this precisely matters — the grant is the boundary
  against other *services*, and the test is the boundary against other *code in the same
  service*. They are not interchangeable and neither covers the other's case.
- Additive changes only, same contract discipline as `core_v1`.

## 2. Writes on the request path

Touching `last_seen_at` is the only write auth performs per request, and it is throttled
— an unthrottled write per request turns every read into a write transaction. Exposed as
`identity_v1.touch_session(p_session_id uuid)`, SECURITY DEFINER and owned by `ap_owner`, so
no API role needs UPDATE on the session table itself. EXECUTE is revoked from PUBLIC and
granted to customer API roles only; `search_path` is pinned on the function. Same rules as
the lookup, for the same reasons.

The throttle interacts with idle timeout: a stale `last_seen_at` makes an idle window
fire **early**. When a tenant configures idle timeout, the touch interval must be shorter
than the window by a clear margin.

## 3. Failure is specific, and suspension is not an authentication failure

Two stages, and conflating them breaks a promise made elsewhere in this repo. Rejecting a
suspended tenant at **authentication** discards the principal — and then the suspended
tenant's admin cannot reach the data export they are explicitly still entitled to, because
there is no longer an authenticated identity to authorize.

So: **authenticate first, always.** A valid session establishes the principal even when
the tenant is suspended. Route authorization then decides what that principal may reach.

| Code | Stage | Status | Meaning |
|---|---|---|---|
| `session_invalid` | authentication | 401 | no row, or expired |
| `session_revoked` | authentication | 401 | revoked elsewhere |
| `role_changed` | authentication | 401 | ticket role ≠ user row |
| `tenant_retired` | authentication | 401 | tenant is gone; sessions revoked with it |
| `mfa_required` | authentication | 401 | enrolment or challenge outstanding |
| `tenant_suspended` | **authorization** | **403** | principal preserved; only the allowlist is reachable |
| `app_not_licensed` | authorization | 403 | tenant has not licensed this app |

**Suspended and retired are different states and must not share a path.**

- *Suspended* is reversible and the customer still owns their data. The principal
  survives, `/api/*/auth/*` and the tenant export stay reachable, everything else is 403,
  and **the cookie is not killed** — a suspended tenant that is resumed should find its
  users still signed in.
- *Retired* is terminal. Sessions are revoked, so the failure is an ordinary 401 and there
  is no allowlist to maintain.

A generic logout for all of these is a support burden — the user cannot tell "your admin
changed your role" from "your session expired" from "your organisation's account is on
hold".

## 4. Cookie isolation

Operators and customers must never share a session.

- Distinct cookie **names** (`ap_op` / `ap_session`) and distinct **host scopes** — the
  console has its own hostname and its cookie is host-only, never domain-wide.
- Distinct signing keys and distinct session tables, so a forged or replayed cookie from
  one surface cannot authenticate the other.
- `HttpOnly`, `Secure`, `SameSite=Lax`, no client-readable session state.

## 5. CSRF

Cookie auth means the browser attaches credentials to cross-site requests, so `SameSite`
alone is not sufficient for state-changing calls.

- `SameSite=Lax` on the session cookie.
- A double-submit token header required on every **unsafe method** — POST, PUT, PATCH,
  DELETE, and anything else outside GET/HEAD/OPTIONS/TRACE. Missing or mismatched is a
  **403** before the handler runs.
- **The test is safe versus unsafe, never idempotent versus not.** PUT and DELETE are
  idempotent and change state; a rule written around idempotency leaves both unprotected,
  which is most of a REST API's write surface.
- Applies to **cookie-authenticated** requests. An API key is not sent automatically by a
  browser, so a key-authenticated call carries no CSRF exposure and requires no token.
- Enforced centrally in `packages/auth`, not per endpoint. A new endpoint must not be
  able to forget it.

## 6. Tenant context

- Established **only** from the validated session — never from a header, query parameter,
  or request body. A client-supplied tenant id is an attack, not a feature.
- Set once per request into the scoped `ITenantProvider` that query filters and stamping
  read.
- **Background jobs have no ambient tenant.** They enter one explicitly per DI scope;
  filters and stamping then behave exactly as in a request.
- **A job that forgets reads nothing and writes nothing** — the filter matches no rows
  without a tenant, and an insert throws `TenantScopeViolationException`. That is required
  behaviour, not an implementation detail: failing empty is recoverable, failing open is a
  breach. The visible symptom is a job that silently does nothing, so a job should assert
  it holds a tenant rather than waiting to notice.
- Internal provisioning endpoints run before any tenant exists. They scope explicitly and
  are reviewed as carefully as anonymous endpoints.

## 6a. The caller, not the user

Every handler receives a `Caller`, never a `Guid userId`:

```
Caller
  PrincipalId    Guid          -- the user or the api key
  Kind           user | api_key
  UserId         Guid?         -- null for a key
  TenantId       int
  CompanyId      int
  Role           string
  Scopes         string[]      -- empty for an interactive user
  EmployeeId     Guid?         -- the self-service link
```

EMS passed a user id because a user was the only thing that could call anything. An API
key is a principal here, and a signature carrying only a user id forces machine calls to
supply a fake one — which lands in audit as a person who did not do it.

- **Audit records the principal**, so "closed by integration key 'phone-bridge'" is
  representable, and revoking that key has a reviewable trail.
- Policies evaluate against `Caller`, so an entitlement or role check is written once and
  applies to both kinds.
- A handler needing a real person (anything self-service) requires `UserId` explicitly and
  fails a key-authenticated call with a clear message, rather than dereferencing a null.

Settled before the first handler exists: threading a new parameter through every feature
file afterwards is the expensive version of this decision.

## 7. Migration ownership

`identity` belongs to **core.api**, with its own history table. Auth is a shared library,
not a service; putting migrations in `packages/auth` would mean a package that owns
schema, which no consumer could reason about.

## 8. Settled values

Defaults, not laws — but written down so they are one edit rather than a re-derivation.

| | Tenant user | Operator |
|---|---|---|
| Absolute session lifetime | 7 days | 8 hours |
| Idle window | off by default; tenant-configurable 5–1440 min | 30 min, not configurable |
| `last_seen_at` touch interval | 5 min, dropping to 1 min when an idle window is set | 1 min |
| MFA | optional per user; required per tenant setting | mandatory |

Two of those interact in a way worth stating plainly. **A stale `last_seen_at` makes an
idle window fire early**, so the touch interval must stay comfortably shorter than the
shortest configurable window — which is why it drops to 1 minute the moment a tenant
configures one, and why the 5-minute default is only safe while the window is off.

An operator's 8-hour absolute lifetime is shorter than a user's 7 days because an operator
session reaches every tenant. The asymmetry is the point.

**Destructive operator actions require re-authentication** — retiring a tenant, rotating a
key, resetting another operator's MFA. A current TOTP code within the last 5 minutes, not
a password, since operator MFA is mandatory anyway. Decided now, built in M2 alongside
operator MFA enforcement.

## 9. Open

- Impersonation ("operator views tenant as admin"): useful for support, dangerous, and
  currently **not designed**. Do not add it casually — it needs its own audit story.
