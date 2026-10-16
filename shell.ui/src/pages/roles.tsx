import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
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
 * same in every organisation and cannot be changed; the organisation can add its own, start one as a
 * copy of any role, and edit or delete its own. Anyone with core.user.read sees the page; changing
 * anything needs core.role.manage, and the API enforces both.
 */
export function Roles() {
  const { session, refresh: refreshSession } = useSession()
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  const [permissions, setPermissions] = useState<readonly PermissionRow[]>([])
  const [loading, setLoading] = useState(true)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [deleting, setDeleting] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
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

  function startNew() {
    setDraft({ roleKey: null, name: '', description: '', permissions: new Set() })
  }

  function startCopy(role: RoleRow) {
    setDraft({
      roleKey: null,
      name: `Copy of ${role.name}`,
      description: role.description,
      permissions: new Set(role.permissions),
    })
  }

  function startEdit(role: RoleRow) {
    setDraft({ roleKey: role.key, name: role.name, description: role.description, permissions: new Set(role.permissions) })
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
      setDeleting(null)
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

  if (loading) return <p role="status">Loading roles…</p>

  const apps = [...new Set(permissions.map((p) => p.app))]

  return (
    <main>
      <h1>Roles</h1>
      {error && <p role="alert">{error}</p>}
      {canManage && !draft && (
        <p>
          <button type="button" onClick={startNew}>
            New role
          </button>
        </p>
      )}

      {draft && (
        <form
          aria-label={draft.roleKey === null ? 'New role' : `Edit ${draft.name}`}
          onSubmit={(e) => {
            e.preventDefault()
            saveDraft()
          }}
        >
          <h2>{draft.roleKey === null ? 'New role' : 'Edit role'}</h2>
          <label style={{ display: 'block' }}>
            Name{' '}
            <input
              value={draft.name}
              maxLength={100}
              required
              onChange={(e) => setDraft({ ...draft, name: e.target.value })}
            />
          </label>
          <label style={{ display: 'block' }}>
            Description{' '}
            <input
              value={draft.description}
              maxLength={500}
              onChange={(e) => setDraft({ ...draft, description: e.target.value })}
            />
          </label>
          {apps.map((app) => (
            <fieldset key={app}>
              <legend>{appTitle(app)}</legend>
              {permissions
                .filter((p) => p.app === app)
                .map((p) => (
                  <label key={p.name} style={{ display: 'block' }}>
                    <input
                      type="checkbox"
                      checked={draft.permissions.has(p.name)}
                      onChange={() => togglePermission(p.name)}
                    />{' '}
                    {p.description}
                  </label>
                ))}
            </fieldset>
          ))}
          <button type="submit" disabled={saving}>
            Save role
          </button>{' '}
          <button type="button" disabled={saving} onClick={() => setDraft(null)}>
            Cancel
          </button>
        </form>
      )}

      {roleGroups(roles).map((group) => (
        <section key={group.title}>
          <h2>{group.title}</h2>
          {group.roles.map((role) => (
            <article key={role.key} aria-label={role.name}>
              <h3>
                {role.name}
                {role.builtIn ? ' (built-in)' : ''}
              </h3>
              {role.description && <p>{role.description}</p>}
              <p>
                Held by {role.holders} {role.holders === 1 ? 'person' : 'people'}.
              </p>
              <ul>
                {role.permissions.map((name) => (
                  <li key={name}>{describe(name)}</li>
                ))}
              </ul>
              {canManage && (
                <p>
                  <button type="button" disabled={saving} onClick={() => startCopy(role)} aria-label={`Copy ${role.name}`}>
                    Copy
                  </button>
                  {!role.builtIn && (
                    <>
                      {' '}
                      <button type="button" disabled={saving} onClick={() => startEdit(role)} aria-label={`Edit ${role.name}`}>
                        Edit
                      </button>{' '}
                      {deleting === role.key ? (
                        // A second click, not a browser confirm(): a native dialog blocks the page
                        // and cannot be styled or tested like the rest of it.
                        <button
                          type="button"
                          disabled={saving}
                          onClick={() =>
                            void write(() => api(`/api/core/v1/roles/${role.key}`, { method: 'DELETE' }), false)
                          }
                        >
                          Confirm: delete {role.name}
                        </button>
                      ) : (
                        <button type="button" disabled={saving} onClick={() => setDeleting(role.key)} aria-label={`Delete ${role.name}`}>
                          Delete
                        </button>
                      )}
                    </>
                  )}
                </p>
              )}
            </article>
          ))}
        </section>
      ))}
    </main>
  )
}
