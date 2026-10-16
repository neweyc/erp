import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import { useSession } from '../components/session-provider'
import { APPS } from '../lib/apps'

interface UserRow {
  readonly userId: string
  readonly email: string
  readonly status: string
  readonly role: string
  readonly appRoles: Readonly<Record<string, string>>
}

interface RoleOption {
  readonly app: string
  readonly name: string
  readonly description: string
}

const CORE = 'core'

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  last_admin: 'That is the only active administrator. Make someone else an administrator first.',
  validation_failed: 'That role cannot be given. Reload the page and try again.',
  not_found: 'That user no longer exists. The list has been reloaded.',
  not_permitted: 'You do not have permission to change roles.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  const code = error instanceof ApiError ? error.problemCode : 'unknown'
  return PROBLEMS[code] ?? 'Something went wrong. Please try again.'
}

function appName(app: string): string {
  return APPS.find((a) => a.id === app)?.name ?? app
}

/**
 * Accounts and their roles: one core role each, and at most one role per licensed app
 * (docs/permissions.md). Anyone with core.user.read sees the page; only core.user.manage can
 * change anything, and the API enforces both.
 *
 * Each select saves on change. There is no form to lose, and a role change is a single
 * reversible step that the audit log records.
 */
export function Users() {
  const { session, refresh: refreshSession } = useSession()
  const [users, setUsers] = useState<readonly UserRow[]>([])
  const [roles, setRoles] = useState<readonly RoleOption[]>([])
  const [loading, setLoading] = useState(true)
  const [savingUserId, setSavingUserId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [userRows, roleRows] = await Promise.all([
        api<UserRow[]>('/api/core/v1/users'),
        api<RoleOption[]>('/api/core/v1/roles'),
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
  const coreRoles = roles.filter((r) => r.app === CORE)
  // The roles endpoint returns only licensed apps, so these are exactly the columns to show.
  const apps = [...new Set(roles.filter((r) => r.app !== CORE).map((r) => r.app))]

  async function change(user: UserRow, body: { role?: string; appRoles?: Record<string, string | null> }) {
    setSavingUserId(user.userId)
    setError(null)
    try {
      await api(`/api/core/v1/users/${user.userId}/roles`, { method: 'PUT', body: JSON.stringify(body) })

      if (user.userId === session!.userId) {
        // Your own roles changed, so your session ends (role_changed) and you sign in again to
        // a shell that matches what you may now do.
        await refreshSession()
        return
      }
    } catch (e) {
      setError(explain(e))
    } finally {
      setSavingUserId(null)
    }
    // Reloaded after a failure too, so a select the server refused shows the real value again
    // rather than the one that was picked.
    await load()
  }

  if (loading) return <p role="status">Loading users…</p>

  return (
    <main>
      <h1>Users</h1>
      {error && <p role="alert">{error}</p>}

      {/* Wide content scrolls inside its own container, never widening the page (CLAUDE.md). */}
      <div style={{ overflowX: 'auto', maxWidth: '100%' }}>
        <table>
          <thead>
            <tr>
              <th scope="col">Email</th>
              <th scope="col">Status</th>
              <th scope="col">Role</th>
              {apps.map((app) => (
                <th key={app} scope="col">
                  {appName(app)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {users.map((user) => {
              const disabled = savingUserId !== null

              return (
                <tr key={user.userId}>
                  <td>{user.email}</td>
                  <td>{user.status}</td>
                  <td>
                    {canManage ? (
                      <select
                        aria-label={`Role for ${user.email}`}
                        value={user.role}
                        disabled={disabled}
                        onChange={(e) => void change(user, { role: e.target.value })}
                      >
                        {coreRoles.map((r) => (
                          <option key={r.name} value={r.name}>
                            {r.name}
                          </option>
                        ))}
                      </select>
                    ) : (
                      user.role
                    )}
                  </td>
                  {apps.map((app) => {
                    const current = user.appRoles[app] ?? ''

                    return (
                      <td key={app}>
                        {canManage ? (
                          <select
                            aria-label={`${appName(app)} role for ${user.email}`}
                            value={current}
                            disabled={disabled}
                            onChange={(e) =>
                              void change(user, { appRoles: { [app]: e.target.value || null } })
                            }
                          >
                            <option value="">No access</option>
                            {roles
                              .filter((r) => r.app === app)
                              .map((r) => (
                                <option key={r.name} value={r.name}>
                                  {r.name}
                                </option>
                              ))}
                          </select>
                        ) : (
                          current || 'No access'
                        )}
                      </td>
                    )
                  })}
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      <h2>What each role allows</h2>
      {[CORE, ...apps].map((app) => (
        <section key={app}>
          <h3>{app === CORE ? 'Organisation' : appName(app)}</h3>
          <dl>
            {roles
              .filter((r) => r.app === app)
              .map((r) => (
                <div key={r.name}>
                  <dt>{r.name}</dt>
                  <dd>{r.description}</dd>
                </div>
              ))}
          </dl>
        </section>
      ))}
    </main>
  )
}
