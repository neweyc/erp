import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import { useSession } from '../components/session-provider'
import { type RoleRow, roleGroups } from '../lib/roles'

interface UserRow {
  readonly userId: string
  readonly email: string
  readonly status: string
  /** Role keys: built-in (`ledger:controller`) or custom (`rol_…`). */
  readonly roles: readonly string[]
}

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  last_admin: 'Nobody else active could manage users after that change. Give someone else that permission first.',
  validation_failed: 'One of those roles cannot be given. Reload the page and try again.',
  not_found: 'That user no longer exists. The list has been reloaded.',
  not_permitted: 'You do not have permission to change roles.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  const code = error instanceof ApiError ? error.problemCode : 'unknown'
  return PROBLEMS[code] ?? 'Something went wrong. Please try again.'
}

/**
 * Accounts and their roles (docs/permissions.md). A user may hold any number of roles, built-in or
 * the organisation's own. Anyone with core.user.read sees the page; only core.user.manage can change
 * anything, and the API enforces both.
 *
 * Editing opens a checklist for one person. Saving sends only what changed, as additions and
 * removals, so a role this page does not list (one for an app since unlicensed) is never touched,
 * and an administrator editing the same person at the same moment loses nothing.
 */
export function Users() {
  const { session, refresh: refreshSession } = useSession()
  const [users, setUsers] = useState<readonly UserRow[]>([])
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<{ user: UserRow; chosen: ReadonlySet<string> } | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [userRows, roleRows] = await Promise.all([
        api<UserRow[]>('/api/core/v1/users'),
        api<RoleRow[]>('/api/core/v1/roles'),
      ])
      setUsers(userRows)
      setRoles(roleRows)
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

  const canManage = session.permissions.includes(Permissions.core.userManage)
  const nameOf = (key: string) => roles.find((r) => r.key === key)?.name

  function toggle(key: string) {
    setEditing((current) => {
      if (!current) return current
      const chosen = new Set(current.chosen)
      if (chosen.has(key)) chosen.delete(key)
      else chosen.add(key)
      return { ...current, chosen }
    })
  }

  async function save() {
    if (!editing) return
    const { user, chosen } = editing
    // Only roles this page lists can have been toggled, so only they can be added or removed.
    const listed = roles.map((r) => r.key)
    const add = listed.filter((key) => chosen.has(key) && !user.roles.includes(key))
    const remove = listed.filter((key) => !chosen.has(key) && user.roles.includes(key))

    setSaving(true)
    setError(null)
    try {
      if (add.length > 0 || remove.length > 0) {
        await api(`/api/core/v1/users/${user.userId}/roles`, {
          method: 'PATCH',
          body: JSON.stringify({ add, remove }),
        })
      }
      setEditing(null)

      if (user.userId === session!.userId) {
        // Your own roles changed, so your session ends (role_changed) and you sign in again to a
        // shell that matches what you may now do.
        await refreshSession()
        return
      }
    } catch (e) {
      setError(explain(e))
    } finally {
      setSaving(false)
    }
    // Reloaded after a failure too, so the page shows what the server holds, not what was picked.
    await load()
  }

  if (loading) return <p role="status">Loading users…</p>

  return (
    <main>
      <h1>Users</h1>
      <p>
        What each role allows is on the <Link to="/roles">Roles</Link> page.
      </p>
      {error && <p role="alert">{error}</p>}

      {/* Wide content scrolls inside its own container, never widening the page (CLAUDE.md). */}
      <div style={{ overflowX: 'auto', maxWidth: '100%' }}>
        <table>
          <thead>
            <tr>
              <th scope="col">Email</th>
              <th scope="col">Status</th>
              <th scope="col">Roles</th>
              {canManage && <th scope="col">Actions</th>}
            </tr>
          </thead>
          <tbody>
            {users.map((user) => (
              <tr key={user.userId}>
                <td>{user.email}</td>
                <td>{user.status}</td>
                <td>
                  {user.roles
                    .map((key) => nameOf(key))
                    .filter((name): name is string => name !== undefined)
                    .join(', ') || 'No roles'}
                </td>
                {canManage && (
                  <td>
                    <button
                      type="button"
                      aria-label={`Edit roles for ${user.email}`}
                      disabled={saving}
                      onClick={() => setEditing({ user, chosen: new Set(user.roles) })}
                    >
                      Edit roles
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {editing && (
        <form
          aria-label={`Roles for ${editing.user.email}`}
          onSubmit={(e) => {
            e.preventDefault()
            void save()
          }}
        >
          <h2>Roles for {editing.user.email}</h2>
          {roleGroups(roles).map((group) => (
            <fieldset key={group.title}>
              <legend>{group.title}</legend>
              {group.roles.map((role) => (
                <label key={role.key} style={{ display: 'block' }}>
                  <input
                    type="checkbox"
                    checked={editing.chosen.has(role.key)}
                    onChange={() => toggle(role.key)}
                  />{' '}
                  <strong>{role.name}</strong> — {role.description || 'No description.'}
                </label>
              ))}
            </fieldset>
          ))}
          <button type="submit" disabled={saving}>
            Save roles
          </button>{' '}
          <button type="button" disabled={saving} onClick={() => setEditing(null)}>
            Cancel
          </button>
        </form>
      )}
    </main>
  )
}
