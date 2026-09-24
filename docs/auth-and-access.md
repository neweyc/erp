# Auth and access revalidation

**Status:** design, v0.1 — must be settled before `packages/auth` is built.

Every API must observe revocation, deactivation, role change, tenant suspension, and
entitlement change **immediately**, not at cookie expiry. Those facts live in `identity`
and `platform` — both outside an app's schema whitelist. This document resolves that.

## 1. The contradiction, and the fix

The architecture says apps may read only their own schema plus `core_v1`. It also says
entitlement changes take effect at once. Taken literally those conflict: an app cannot
check `platform.tenant_app` per request.

**Fix: one more published view.** `identity_v1.session_context` joins session, user,
tenant, and entitlements. Apps are granted SELECT on the view and on none of the
underlying tables.

```
identity_v1.session_context
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

- Owned by core's migrations (it owns `identity`), even though it reads `platform`
  tables. The view is the only cross-schema reach in the system and is deliberate.
- Granted SELECT to every API role. Underlying tables are granted to nobody but their
  owner.
- **Only `packages/auth` reads it.** `BoundaryTests` plus the grants both enforce that.
- Additive changes only, same contract discipline as `core_v1`.

## 2. Writes on the request path

Touching `last_seen_at` is the only write auth performs per request, and it is throttled
— an unthrottled write per request turns every read into a write transaction. Exposed as
a narrow `identity.touch_session(session_id)` function granted to API roles, so no role
needs UPDATE on the session table itself.

The throttle interacts with idle timeout: a stale `last_seen_at` makes an idle window
fire **early**. When a tenant configures idle timeout, the touch interval must be shorter
than the window by a clear margin.

## 3. Failure is specific

Validation failure returns **401** (never 302) with a stable `problemCode`:

| Code | Meaning | Shell behaviour |
|---|---|---|
| `session_invalid` | no row, or expired | sign in |
| `session_revoked` | revoked elsewhere | sign in, say so |
| `role_changed` | ticket role ≠ user row | sign in, say so |
| `tenant_suspended` | tenant not active | suspended screen; auth and export still reachable |
| `mfa_required` | enrolment or challenge outstanding | MFA step |
| `app_not_licensed` | **403**, not 401 | app hidden from nav |

A generic logout for all six is a support burden — the user cannot tell "your admin
changed your role" from "your session expired".

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
- A double-submit token header required on every non-idempotent request; missing or
  mismatched is a **403** before the handler runs.
- Enforced centrally in `packages/auth`, not per endpoint. A new endpoint must not be
  able to forget it.

## 6. Tenant context

- Established **only** from the validated session — never from a header, query parameter,
  or request body. A client-supplied tenant id is an attack, not a feature.
- Set once per request into the scoped `ITenantProvider` that query filters and stamping
  read.
- **Background jobs have no ambient tenant.** They enter one explicitly per DI scope;
  filters and stamping then behave exactly as in a request. A job that forgets runs
  unfiltered across every tenant — the classic version of this bug.
- Internal provisioning endpoints run before any tenant exists. They scope explicitly and
  are reviewed as carefully as anonymous endpoints.

## 7. Migration ownership

`identity` belongs to **core.api**, with its own history table. Auth is a shared library,
not a service; putting migrations in `packages/auth` would mean a package that owns
schema, which no consumer could reason about.

## 8. Open

- Session absolute lifetime and sliding window values.
- Whether operator sessions need per-action re-authentication for destructive actions.
- Impersonation ("operator views tenant as admin"): useful for support, dangerous, and
  currently **not designed**. Do not add it casually — it needs its own audit story.
