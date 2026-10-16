# Permissions

Who may do what inside a tenant. Written before building, as `CLAUDE.md` asks of auth. Decided by
Chris on 2026-10-15: **per-app roles, fixed in code**, with a Users page in this cycle. The
operator console has its own model and is out of scope here.

## 1. The shape

```
permission   one action, named <app>.<thing>.<verb>       ledger.period.close
role         a fixed, named bundle of permissions, per app ledger: controller
user         one core role + at most one role per app      core: member, ledger: accountant
```

- **Code checks permissions, never roles.** An endpoint says `.RequirePermission(Permissions.Ledger.PeriodClose)`;
  nothing anywhere says `if role == "admin"`. That is what keeps later changes cheap: moving an
  action between roles, adding a role, or one day letting tenants define their own roles changes
  the catalogue, not the features.
- **Roles are per app.** A user holds one **core** role and **at most one role in each app**.
  Someone can be a ledger controller without being a tenant administrator, and the tenant
  administrator does not automatically post journals.
- **The catalogue is code**, in `packages/auth/Permissions.cs`: every permission, every role, and
  which permissions each role grants. A role is not a database row in v1. Tenant-defined roles
  would add tables, a role editor, and a much larger test surface; nobody has asked for them. If a
  customer does, roles move into data and **no endpoint changes**, because endpoints only ever
  named permissions.

## 2. The catalogue

Permission names are API surface: the session returns them and the UI checks them. Renaming one is
a breaking change, like a problem code.

**Core** (always on). The role is `identity.user.role`, which already exists.

| Permission | admin | manager | member |
|---|---|---|---|
| `core.employee.read` — the roster | ✓ | ✓ | ✓ |
| `core.employee.create` | ✓ | ✓ | |
| `core.user.read` — accounts and their roles | ✓ | | |
| `core.user.manage` — invite, change roles | ✓ | | |

**Tickets**

| Permission | requester | agent |
|---|---|---|
| `tickets.ticket.read` | ✓ | ✓ |
| `tickets.ticket.create` | ✓ | ✓ |
| `tickets.ticket.assign` | | ✓ |
| `tickets.ticket.close` | | ✓ |

**Ledger**

| Permission | viewer | accountant | controller |
|---|---|---|---|
| `ledger.read` — accounts, entries, trial balance, periods | ✓ | ✓ | ✓ |
| `ledger.entry.post` | | ✓ | ✓ |
| `ledger.entry.reverse` | | ✓ | ✓ |
| `ledger.account.create` | | | ✓ |
| `ledger.period.close` | | | ✓ |

This matrix is a starting proposal. Changing it is a one-file edit plus the tests that pin it.

What it changes about today's behaviour:

- **D18 closes.** A member can no longer create employees.
- Inviting stays admin-only (now `core.user.manage`).
- Closing a period moves from "core admin" to "ledger controller".
- **Nobody has an app role until one is assigned.** Licensing an app grants no one anything. The
  tenant administrator assigns roles, their own included, on the Users page. An implicit grant
  ("admins get everything") is the kind of access nobody remembers giving.

## 3. Where roles live

- Core role: `identity.user.role` (unchanged).
- App roles: `identity.user.app_roles text[]`, entries such as `tickets:agent`, default empty.
  It is a column on the user rather than its own table, because a role change is then an ordinary
  audited update of the user: the audit row records old and new roles with no extra machinery.
  The handler keeps it at one entry per app. The column is additive, so it is safe under
  expand/contract.
- `identity.user.access_version`, advanced by every role change. It numbers the
  `user.roles_changed` events. It is **not** an EF concurrency token: a token is checked on every
  update of the row, so it would fail an invitation accepted while the user's roles changed (§6).
- `identity_v1.session_context` returns `app_roles` beside `role`. Changing a function's
  `RETURNS TABLE` means DROP and CREATE, so the new migration runs as `ap_owner` and restores
  EXECUTE to the three customer runtime roles in the same script.

## 4. How a request is decided

1. **Authentication** (unchanged) resolves the session through `session_context`.
2. `SessionEvaluator` computes `Caller.Permissions`: the core role's permissions, plus each app
   role's permissions **for apps the tenant currently licenses**. An unlicensed app's role grants
   nothing, so delicensing removes access even before the entitlement check runs. **An unknown
   role name grants nothing.** That can happen if an older build reads a role a newer build wrote,
   or after a role is removed from the catalogue. Failing closed is recoverable; failing open is not.
3. **Permission**: `PermissionMiddleware`, installed by `UseAppPlatformAuth`, reads the endpoint's
   `RequiredPermission` metadata and answers **403 `not_permitted`** when the caller lacks it.
   **Handlers never check permissions**, for the same reason they never check entitlements.
   It runs **before** the entitlement check, so for a permission in an app the tenant does not
   license it answers `app_not_licensed` itself: such a permission is never granted, and "not
   licensed" tells an administrator what to fix where "not permitted" would not.
4. **Entitlement** (`RequireApp`, unchanged) then runs as before. Either way an unlicensed tenant
   gets `app_not_licensed`.

**Every authenticated customer endpoint must declare one of:**

- `.RequirePermission(p)`, or
- `.AllowAnySignedInUser()`, for the few routes that are about the caller themselves: the session
  and sign-out.

An authenticated endpoint that declares neither is **refused at runtime** with 403 and an error
log, and **fails the build** in `boundary.tests/PermissionCoverageTests`, which maps every
customer service's real route table. A forgotten declaration cannot fail open.

**Row-level rules stay in handlers.** "Only the assignee may close" or "a manager sees their own
department" are business rules about a particular record, not about the action. None exist
today; when one does, it lives in its slice, beside the record it reads.

## 5. Role changes take effect immediately

Permissions are recomputed from the live row on every request, so a demotion needs no session
revocation to be effective.

The cookie also carries an **access claim**, a canonical string of the core role and sorted app
roles recorded at sign-in. It replaces today's role claim. When it disagrees with the live row,
the request fails with the existing `role_changed`, the shell sends the user to sign in, and the
navigation they then see matches what they may do. This comparison is about the **shell's view**,
not about security: security comes from the live recomputation. A cookie with no access claim
skips the comparison and remains bound by live permissions, which is the existing rule.

## 6. Changing roles

`PUT /api/core/v1/users/{userId}/roles`, requiring `core.user.manage`:

```json
{ "role": "manager", "appRoles": { "tickets": "agent", "ledger": null } }
```

- `role` is optional. Only the apps named in `appRoles` change; `null` removes that app's role.
  A role held in an app the tenant has since unlicensed is therefore never silently erased by a
  page that does not show it.
- Each name is validated against the catalogue. Granting a role in an **unlicensed** app is
  refused. Removing one is allowed. An administrator may change their own roles, subject to
  the next rule.
- **Role changes in a tenant run one at a time.** Each takes a per-tenant lock (a transaction
  advisory lock) before it reads anything, so it always starts from the previous change's result.
  One lock rather than row locks in an agreed order: the rows a change needs (the admins) can be
  changed by the change ahead of it, and no order can be agreed for rows a waiter has not seen,
  so row locks alone could deadlock when a promotion was in flight.
- **The tenant always keeps an active admin.** A change that would leave none is refused with
  `last_admin`. The check reads the admins under the tenant lock and also locks their rows
  (`SELECT … FOR UPDATE`), so two admins demoting each other at the same moment cannot both
  succeed, and no other write to an admin can land between the check and the commit.
- Invited (not yet active) users can be given app roles in advance.
- The user row's audit entry records the change. A `user.roles_changed` event goes to the
  outbox in the same save.
- Two administrators changing the **same** user at once: the second waits for the tenant lock,
  reads the first one's result, and applies only what it names on top. Nothing is silently
  overwritten and nobody gets an error. Other writes to the user, such as accepting an invitation
  or a password rehash, take no lock and write other columns, so a role change never makes them
  fail.
- A request that changes nothing saves nothing: no event, and the user's session is not ended.

Supporting reads: `GET /api/core/v1/users` (`core.user.read`) lists accounts with status and
roles. `GET /api/core/v1/roles` (`core.user.read`) returns the catalogue for the tenant's
licensed apps, so the Users page has a single source for role names and descriptions.

## 7. The shell

- The session payload adds `appRoles` and `permissions`, the latter computed server-side.
  The shell never derives permissions from roles itself, so there is one implementation of that
  rule.
- Permission names are mirrored as constants in `packages/web-api/src/permissions.ts`, which every
  UI package already depends on. `PermissionCatalogueTests` fails when the TypeScript list and the
  .NET catalogue disagree.
- An app's registry entry names the permission needed to **see** it (for example
  `tickets.ticket.read`) in place of today's role list. App components receive the caller's
  permissions and **hide** actions the user cannot take. Hiding is a convenience; the API
  enforces.
- A **Users** page in the shell (core), visible with `core.user.read`.

## 8. API keys (designed, not built)

When API keys arrive (M3.5), a key's **scopes are permission names** from this same catalogue,
and `Caller.Permissions` is the key's scopes intersected with the licensed apps. One authorization
model for both kinds of principal, as `docs/integration.md` requires.

## 9. Not in this cycle

- Tenant-defined roles (see §1).
- Row-level rules (see §4).
- Deactivating a user from the Users page. The status exists; no endpoint changes it yet.
- Operator roles in the platform console.
- Narrowing roster fields by permission, such as hiding email addresses from members. That is
  still an open question, now a small change: split `core.employee.read`.
