# Permissions

Who may do what inside a tenant. Written before building, as `CLAUDE.md` asks of auth. The
operator console has its own model and is out of scope here.

Decisions, all by Chris:

- 2026-10-15: permissions, not roles, are what code checks; a Users page (Cycle 12).
- 2026-10-16: **customer-defined roles** (Cycle 13). A user holds **any number of roles**, a role
  may span apps, and a user's permissions are the union. The **built-in roles stay, locked**, in
  every tenant; customers add their own alongside. Customers may define **core** roles too.

## 1. The shape

```
permission   one action, named <app>.<thing>.<verb>              ledger.period.close
role         a named bundle of permissions, from any apps        "Finance clerk"
user         any number of roles; may do what any of them grants  core:member + Finance clerk
```

- **Code checks permissions, never roles.** An endpoint says
  `.RequirePermission(Permissions.Ledger.PeriodClose)`; nothing anywhere says `if role == "admin"`.
  This is what made custom roles possible without touching a single endpoint.
- **Built-in roles** are code (`packages/auth/Permissions.cs`), identical in every tenant, and
  cannot be edited or deleted. A release that adds a permission to a built-in role reaches every
  tenant at once. A customer who wants a variation copies one and edits the copy.
- **Custom roles** are data: rows in `identity.role`, owned by one tenant.
- **The permission catalogue stays in code.** A permission only means something if an endpoint
  checks it, so customers combine permissions; they never invent them.
- A user's permissions are the **union** of their roles' permissions, restricted to core and the
  apps the tenant currently licenses. There is no "deny": a role can only add.

## 2. The catalogue

Permission names are API surface: the session returns them and the UI checks them. Renaming one is
a breaking change, like a problem code. Each has a description in code, shown in the role editor.

| Permission | Description |
|---|---|
| `core.employee.read` | See the employee roster |
| `core.employee.create` | Add employees |
| `core.user.read` | See accounts, their roles, and the roles defined |
| `core.user.manage` | Invite people and change anyone's roles |
| `core.role.manage` | Create, edit and delete the organisation's own roles |
| `tickets.ticket.read` | See tickets |
| `tickets.ticket.create` | Raise tickets |
| `tickets.ticket.assign` | Assign tickets |
| `tickets.ticket.close` | Close tickets |
| `ledger.read` | See accounts, entries, the trial balance and periods |
| `ledger.entry.post` | Post journal entries |
| `ledger.entry.reverse` | Reverse journal entries |
| `ledger.account.create` | Add accounts to the chart |
| `ledger.period.close` | Close accounting periods |

**Built-in roles.** Each covers one app. Keys are `<app>:<name>`, which keeps them unique across
apps and distinct from custom role ids. A colon, where permissions use dots, so a role key and a
permission name are never confused.

| Key | Grants |
|---|---|
| `core:admin` | every core permission, `core.role.manage` included |
| `core:manager` | `core.employee.read`, `core.employee.create` |
| `core:member` | `core.employee.read` |
| `tickets:requester` | read, create |
| `tickets:agent` | read, create, assign, close |
| `ledger:viewer` | read |
| `ledger:accountant` | read, post, reverse |
| `ledger:controller` | everything in the ledger |

**Licensing grants no one anything.** The tenant administrator assigns roles, their own included,
on the Users page. An implicit grant ("admins get everything") is the kind of access nobody
remembers giving.

## 3. Where roles live

- `identity.role`: the tenant's custom roles. Public id `rol_…`, name (unique in the tenant,
  ignoring case), description, `permissions text[]`. Tenant-scoped, audited, and every write
  emits `role.created`, `role.updated` or `role.deleted`.
- `identity.user.roles text[]`: the keys of the roles a user holds, built-in (`ledger:controller`)
  or custom (`rol_…`). It replaces Cycle 12's `role` and `app_roles` columns: the migration
  converts them (`admin` becomes `core:admin`; `tickets:agent` is already a key) and drops
  them. Dropping in the same release is acceptable only because nothing is deployed and the one
  supported combination is "everything from the same commit" (`docs/compatibility.md`). From the
  first customer deployment the same change would take two releases (expand/contract).
  A column rather than a join table, as in Cycle 12: a change of roles is then an ordinary audited
  update of the user, with old and new values in the audit row.
- `identity.user.access_version`, advanced by every change to the user's roles. It numbers the
  `user.roles_changed` events. Not a concurrency token (see §6).
- `identity_v1.session_context` returns `roles` and `role_permissions`: the union of the
  permissions of the user's **custom** roles, joined on the user's own tenant. Built-in roles are
  resolved in code. The function's `RETURNS TABLE` changes, so its migration is DROP and CREATE,
  applied as `ap_owner`, restoring EXECUTE to the three customer runtime roles in the same script.

## 4. How a request is decided

1. **Authentication** (unchanged) resolves the session through `session_context`.
2. `SessionEvaluator` computes `Caller.Permissions`: the permissions of each built-in role the user
   holds, plus `role_permissions`. It then keeps only permissions that (a) this build's catalogue
   knows, and (b) belong to core or an app the tenant currently licenses. It **fails closed** at
   every step. An unknown role key grants nothing, and so does a permission since removed from the
   catalogue but still stored in a custom role. A custom role deleted while someone held it would
   grant nothing too, although §6 refuses that.
3. **Permission**: `PermissionMiddleware`, installed by `UseAppPlatformAuth`, reads the endpoint's
   `RequiredPermission` metadata and answers **403 `not_permitted`** when the caller lacks it.
   **Handlers never check permissions.** It runs **before** the entitlement check, so for a
   permission in an app the tenant does not license it answers `app_not_licensed` itself.
4. **Entitlement** (`RequireApp`, unchanged) then runs as before.

**Every authenticated customer endpoint must declare one of** `.RequirePermission(p)` or, for
the few routes about the caller themselves (the session, sign-out), `.AllowAnySignedInUser()`. An
authenticated endpoint that declares neither is **refused at runtime** with 403 and an error log,
and **fails the build** in `boundary.tests/PermissionCoverageTests`.

**Row-level rules stay in handlers.** "Only the assignee may close" is a business rule about a
record, not an action. None exist today.

## 5. Changes take effect immediately

Permissions are recomputed from the live rows on every request. That covers a change to a user's
roles and an edit to a role they hold, with no session revocation needed.

The cookie also carries an **access claim**: a hash of the user's permissions at sign-in, before
the licensing filter. When the live value differs, the request fails with `role_changed`, the shell
sends the user to sign in, and the navigation they then see matches what they may do. So editing a
custom role signs out the people who hold it, which is the honest outcome: what they may do just
changed. Licensing changes do not sign anyone out; the shell already re-reads entitlements.
Security does not rest on the claim. A cookie with no claim skips the comparison and remains
bound by live permissions.

## 6. Managing roles

### Assigning roles to a user

`PATCH /api/core/v1/users/{userId}/roles`, requiring `core.user.manage`:

```json
{ "add": ["ledger:controller", "rol_…"], "remove": ["core:manager"] }
```

- **Additions and removals, not a replacement list.** Two administrators changing one person at
  the same moment both get what they asked for. Nobody overwrites a role they never saw, such as
  a built-in role for an app the tenant no longer licenses, which the page does not list.
- Each key must be a built-in role or one of the tenant's custom roles. A built-in role in an
  **unlicensed** app cannot be added: it would grant nothing today, and something unexpected on
  the day the app is licensed. Removing anything is allowed. Removing a role not held is a no-op.
- A request that changes nothing saves nothing: no event, and no sign-out.
- The audit row records old and new roles; `user.roles_changed` goes to the outbox in the same save.

### Defining roles

Requiring `core.role.manage`:

- `POST /api/core/v1/roles` with `{ name, description, permissions }`.
- `PUT /api/core/v1/roles/{roleId}`, same body, replacing name, description and permissions.
  Permissions in apps the tenant **does not license** cannot be added. Ones the role already holds
  are **kept**: the editor shows only licensed apps, so it cannot erase what it does not show.
- `DELETE /api/core/v1/roles/{roleId}`: refused with `role_in_use` while anyone holds the role. The
  administrator removes it from those people first. Deleting silently would also change their
  access silently.
- A name must be non-empty, at most 100 characters, unique in the tenant ignoring case, and must
  not match a built-in role's name. Two roles called "Agent" would make the Users page ambiguous.
- Built-in roles cannot be changed. `/roles/{roleId}` takes custom role ids only.

Reads, requiring `core.user.read`:

- `GET /api/core/v1/roles`: built-in roles for core and licensed apps, then the tenant's custom
  roles, each with its permissions and the number of users holding it.
- `GET /api/core/v1/permissions`: the catalogue with descriptions, for core and licensed apps.
- `GET /api/core/v1/users`: accounts with status and role keys.

### The tenant always keeps someone who can manage users

After any change, at least one **active** user must hold `core.user.manage`, or nobody can fix
access again without operator help. A change that would leave none is refused with `last_admin`.
The check applies to taking a role from a user and to editing a role so that it stops granting
`core.user.manage`. (Deleting a held role is already refused.)

**Every role write in a tenant takes the tenant's role-change lock first**, a transaction advisory
lock, before reading anything. That covers assigning, creating, editing and deleting. Writes
therefore run one at a time, each starts from the previous one's result, and the check above
reads a set no concurrent role write can change. One lock per tenant, rather than row locks: the
rows a check depends on (who can manage users) are themselves changed by the writes being
serialized, so no row order can be agreed in advance. Cycle 12's review found exactly that
deadlock.

Other writes to a user, such as accepting an invitation or a password rehash, take no lock and write
other columns, so a role change never makes them fail. Accepting an invitation can only *add* an
active user. Any future write that can *remove* one, such as deactivation or termination, **must
take the same lock** and run the same check.

### Who may grant what

`core.user.manage` and `core.role.manage` are **security administrator** permissions. Whoever holds
`core.user.manage` may assign any role to anyone, themselves included. Whoever holds
`core.role.manage` may put any licensed permission in a custom role. Neither is limited to
permissions its holder already has. That is the built-in administrator's power today, and the
model of NetSuite's Administrator and Dynamics' System Administrator. A non-escalation rule ("you may
only grant what you hold") cannot coexist with "licensing grants nobody anything": the first
administrator could then never be given an app role at all. Recorded in `docs/open-questions.md`
in case a customer needs delegated administration.

## 7. Invitations and provisioning

- Provisioning gives the first user `core:admin`.
- `POST …/employees/{id}/invite` takes `roles` (role keys, default `["core:member"]`), validated as
  for assignment, and requires `core.user.manage`.

## 8. The shell

- The session payload carries `roles` (key and display name) and `permissions`, computed by the
  server. The shell never derives permissions from roles.
- Permission names are mirrored as constants in `packages/web-api/src/permissions.ts`, kept in step
  by `PermissionCatalogueTests`. Descriptions come from `GET /permissions`, so they have one source.
- An app's registry entry names the permission needed to see it; app components receive the
  caller's permissions and hide what the user cannot do. Hiding is a convenience; the API enforces.
- **Users** page (`core.user.read`): each account with its roles. With `core.user.manage`, an
  editor lists every assignable role as a checkbox, with its description, and saves the
  differences as `add`/`remove`.
- **Roles** page (`core.user.read`): built-in and custom roles, what each grants, and how many
  people hold each. With `core.role.manage`: create a role, start one as a copy of any role, and edit
  or delete custom roles. The permission checklist is grouped by app and shows each description.

## 9. API keys (designed, not built)

When API keys arrive (M3.5), a key's **scopes are permission names** from this same catalogue, and
`Caller.Permissions` is the key's scopes intersected with the licensed apps. One authorization model
for both kinds of principal, as `docs/integration.md` requires.

## 10. Not in this cycle

- Delegated administration, that is, a non-escalation rule (§6).
- Row-level rules (§4).
- Deactivating a user from the Users page. When built, it takes the role-change lock (§6).
- Operator roles in the platform console.
- Narrowing roster fields by permission, such as hiding email addresses from members. Still open,
  and now a small change: split `core.employee.read`.
