import { NavLink } from 'react-router-dom'
import { Permissions } from '@app-platform/web-api'
import { visibleApps } from '../lib/apps'
import { useSession } from './session-provider'

export function AppNav() {
  const { session } = useSession()

  if (!session) return null

  const apps = visibleApps(session.licensedApps, session.permissions)

  return (
    <nav aria-label="Applications">
      <ul>
        <li>
          <NavLink to="/dashboard">Dashboard</NavLink>
        </li>
        {/* Unavailable apps are ABSENT, not disabled. A disabled item advertises something the
            reader cannot have and invites a support conversation about it. */}
        {apps.map((app) => (
          <li key={app.id}>
            <NavLink to={app.path}>{app.name}</NavLink>
          </li>
        ))}
        {session.permissions.includes(Permissions.core.userRead) && (
          <li>
            <NavLink to="/users">Users</NavLink>
          </li>
        )}
      </ul>
    </nav>
  )
}
