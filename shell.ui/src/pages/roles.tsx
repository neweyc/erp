import { useCallback, useEffect, useState } from 'react'
import { Check, Copy, Lock, Pencil, Plus, Trash, Users } from 'lucide-react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import {
  Alert, Badge, Button, Card, CardFooter, Checkbox, CheckboxRow, ConfirmDialog, Dialog, DialogBody, DialogFooter,
  Field, Input, Loading, PageHeader, PageStack,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { appTitle, type PermissionRow, type RoleRow, roleGroups } from '../lib/roles'

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  role_name_taken: 'Another role already has that name.',
  role_in_use: 'Someone still holds that role. Remove it from them on the Users page first.',
  last_admin: 'Nobody active could manage users after that change. Give someone else that permission first.',
  not_found: 'That role no longer exists. The list has been reloaded.',
  not_permitted: 'You do not have permission to change roles.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.'
  // A validation failure's message names the field at fault, which beats a generic sentence.
  if (error.problemCode === 'validation_failed' && error.message) return error.message
  return PROBLEMS[error.problemCode] ?? 'Something went wrong. Please try again.'
}

interface Draft {
  /** The role being edited, or null for a new one (including a copy). */
  readonly roleKey: string | null
  readonly name: string
  readonly description: string
  readonly permissions: ReadonlySet<string>
}

/**
 * The roles a user can be given and what each allows (docs/permissions.md). Built-in roles are the
 * same in every organization and cannot be changed; the organization can add its own, start one as a
 * copy of any role, and edit or delete its own. Anyone with core.user.read sees the page; changing
 * anything needs core.role.manage, and the API enforces both.
 */
export function Roles() {
  const { session, refresh: refreshSession } = useSession()
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  const [permissions, setPermissions] = useState<readonly PermissionRow[]>([])
  const [loading, setLoading] = useState(true)
  const [draft, setDraft] = useState<Draft | null>(null)
  /** The role whose Delete button was clicked: the confirming dialog is open for it. */
  const [deleting, setDeleting] = useState<RoleRow | null>(null)
  const [saving, setSaving] = useState(false)
  /** A failure while the editor is open is shown inside it; any other on the page. */
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [roleRows, permissionRows] = await Promise.all([
        api<RoleRow[]>('/api/core/v1/roles'),
        api<PermissionRow[]>('/api/core/v1/permissions'),
      ])
      setRoles(roleRows)
      setPermissions(permissionRows)
    } catch (e) {
      setError(explain(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  if (!session) return null

  const canManage = session.permissions.includes(Permissions.core.roleManage)
  const describe = (name: string) => permissions.find((p) => p.name === name)?.description ?? name

  function open(next: Draft) {
    setError(null)
    setDraft(next)
  }

  function togglePermission(name: string) {
    setDraft((current) => {
      if (!current) return current
      const chosen = new Set(current.permissions)
      if (chosen.has(name)) chosen.delete(name)
      else chosen.add(name)
      return { ...current, permissions: chosen }
    })
  }

  /** Runs a write, then reloads. Changing a role you hold ends your session, as it does for everyone holding it. */
  async function write(request: () => Promise<unknown>, affectsMe: boolean) {
    setSaving(true)
    setError(null)
    try {
      await request()
      setDraft(null)
      if (affectsMe) {
        await refreshSession()
        return
      }
    } catch (e) {
      setError(explain(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  function saveDraft() {
    if (!draft) return
    // Only permissions the editor lists are sent; the server keeps any the role holds in apps
    // since unlicensed, which this page does not show.
    const body = JSON.stringify({
      name: draft.name,
      description: draft.description,
      permissions: permissions.map((p) => p.name).filter((name) => draft.permissions.has(name)),
    })
    const heldByMe = draft.roleKey !== null && session!.roles.some((r) => r.key === draft.roleKey)

    void write(
      () =>
        draft.roleKey === null
          ? api('/api/core/v1/roles', { method: 'POST', body })
          : api(`/api/core/v1/roles/${draft.roleKey}`, { method: 'PUT', body }),
      heldByMe,
    )
  }

  function deleteRole(role: RoleRow) {
    setDeleting(null)
    void write(() => api(`/api/core/v1/roles/${role.key}`, { method: 'DELETE' }), false)
  }

  if (loading) return <Loading>Loading roles…</Loading>

  const apps = [...new Set(permissions.map((p) => p.app))]

  return (
    <PageStack>
      <PageHeader
        title="Roles"
        description="A role is a named set of permissions. Built-in roles are the same everywhere; your organization can add its own."
        actions={
          canManage && (
            <Button
              variant="primary"
              onClick={() => open({ roleKey: null, name: '', description: '', permissions: new Set() })}
            >
              <Plus /> New role
            </Button>
          )
        }
      />

      {error && !draft && <Alert tone="danger">{error}</Alert>}

      {roleGroups(roles).map((group) => (
        <section key={group.title} aria-label={group.title} className="grid gap-3">
          <h2 className="text-sm font-medium text-muted-foreground">{group.title}</h2>
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {group.roles.map((role) => (
              <Card key={role.key} className="flex flex-col">
                <article aria-label={role.name} className="flex flex-1 flex-col">
                  <div className="flex flex-1 flex-col gap-3 p-5">
                    <div className="flex items-start justify-between gap-3">
                      <h3 className="font-semibold tracking-tight">{role.name}</h3>
                      {role.builtIn ? (
                        <Badge tone="neutral">
                          <Lock /> Built-in
                        </Badge>
                      ) : (
                        <Badge tone="brand">Custom</Badge>
                      )}
                    </div>
                    {role.description && <p className="-mt-1 text-sm text-muted-foreground">{role.description}</p>}
                    <ul className="grid gap-1.5 text-sm">
                      {role.permissions.map((name) => (
                        <li key={name} className="flex items-start gap-2">
                          <Check className="mt-0.5 size-4 shrink-0 text-success" />
                          <span>{describe(name)}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                  <CardFooter className="flex-nowrap gap-1 py-2">
                    <p className="mr-auto flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground">
                      <Users className="size-3.5 shrink-0" />
                      Held by {role.holders} {role.holders === 1 ? 'person' : 'people'}.
                    </p>
                    {canManage && (
                      <>
                        <Button
                          size="sm"
                          variant="ghost"
                          disabled={saving}
                          aria-label={`Copy ${role.name}`}
                          onClick={() =>
                            open({
                              roleKey: null,
                              name: `Copy of ${role.name}`,
                              description: role.description,
                              permissions: new Set(role.permissions),
                            })
                          }
                        >
                          <Copy /> Copy
                        </Button>
                        {!role.builtIn && (
                          <>
                            <Button
                              size="sm"
                              variant="ghost"
                              disabled={saving}
                              aria-label={`Edit ${role.name}`}
                              onClick={() =>
                                open({
                                  roleKey: role.key,
                                  name: role.name,
                                  description: role.description,
                                  permissions: new Set(role.permissions),
                                })
                              }
                            >
                              <Pencil /> Edit
                            </Button>
                            <Button
                              size="icon"
                              variant="ghost"
                              className="size-8 text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                              disabled={saving}
                              aria-label={`Delete ${role.name}`}
                              title="Delete"
                              onClick={() => setDeleting(role)}
                            >
                              <Trash />
                            </Button>
                          </>
                        )}
                      </>
                    )}
                  </CardFooter>
                </article>
              </Card>
            ))}
          </div>
        </section>
      ))}

      {draft && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setDraft(null)}
          // Holds a part-made role: a stray click outside must not discard it.
          dismissible={false}
          title={draft.roleKey === null ? 'New role' : 'Edit role'}
          description="Choose what holders of this role may do. They get it the next time they sign in."
          size="lg"
        >
          <form
            aria-label={draft.roleKey === null ? 'New role' : `Edit ${draft.name}`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault()
              saveDraft()
            }}
          >
            <DialogBody className="grid gap-5">
              {error && <Alert tone="danger">{error}</Alert>}
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Name" htmlFor="role-name">
                  <Input
                    id="role-name"
                    value={draft.name}
                    maxLength={100}
                    required
                    onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                  />
                </Field>
                <Field label="Description" htmlFor="role-description">
                  <Input
                    id="role-description"
                    value={draft.description}
                    maxLength={500}
                    onChange={(e) => setDraft({ ...draft, description: e.target.value })}
                  />
                </Field>
              </div>
              {apps.map((app) => (
                <fieldset key={app} className="grid gap-1">
                  <legend className="mb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">
                    {appTitle(app)}
                  </legend>
                  {permissions
                    .filter((p) => p.app === app)
                    .map((p) => (
                      <CheckboxRow
                        key={p.name}
                        control={
                          <Checkbox checked={draft.permissions.has(p.name)} onChange={() => togglePermission(p.name)} />
                        }
                        title={p.description}
                      />
                    ))}
                </fieldset>
              ))}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setDraft(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Saving…' : 'Save role'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(isOpen) => !isOpen && setDeleting(null)}
        title={`Delete ${deleting?.name ?? ''}?`}
        description="The role is removed for good. It can be deleted only while nobody holds it."
        confirmLabel="Delete role"
        confirmAccessibleName={deleting ? `Confirm: delete ${deleting.name}` : undefined}
        busy={saving}
        onConfirm={() => deleting && deleteRole(deleting)}
      />
    </PageStack>
  )
}
