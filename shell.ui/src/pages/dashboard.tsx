import { Link } from 'react-router-dom'
import { Permissions } from '@app-platform/web-api'
import { useSession } from '../components/session-provider'
import { APPS, visibleApps } from '../lib/apps'

export function Dashboard() {
  const { session } = useSession()

  if (!session) return null

  const available = visibleApps(session.licensedApps, session.permissions)
  const unlicensed = APPS.filter((app) => !session.licensedApps.includes(app.id)).length

  // Licensing an app grants nobody a role in it, the administrator included. Without this an
  // administrator who has just licensed an app sees nothing change and concludes it is broken.
  const licensedWithoutRole = APPS.filter(
    (app) => session.licensedApps.includes(app.id) && !available.includes(app),
  )
  const canManageUsers = session.permissions.includes(Permissions.core.userManage)

  return (
    <main>
      <h1>{session.tenantName}</h1>
      <p>
        Signed in as {session.email} ({session.role})
      </p>

      {available.length === 0 ? (
        // An empty state that says what to do. "No applications" alone reads as a fault.
        <p>No applications are available to you yet. Your administrator can give you access.</p>
      ) : (
        <ul>
          {available.map((app) => (
            <li key={app.id}>{app.name}</li>
          ))}
        </ul>
      )}

      {canManageUsers && licensedWithoutRole.length > 0 && (
        <p>
          You have no role yet in {licensedWithoutRole.map((app) => app.name).join(', ')}. Give
          yourself or others one on the <Link to="/users">Users</Link> page.
        </p>
      )}

      {unlicensed > 0 && <p>{unlicensed} further application(s) are available to license.</p>}
    </main>
  )
}
