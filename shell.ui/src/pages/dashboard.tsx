import { useSession } from '../components/session-provider'
import { APPS, visibleApps } from '../lib/apps'

export function Dashboard() {
  const { session } = useSession()

  if (!session) return null

  const available = visibleApps(session.licensedApps, session.role)
  const unlicensed = APPS.length - available.length

  return (
    <main>
      <h1>{session.tenantName}</h1>
      <p>
        Signed in as {session.email} ({session.role})
      </p>

      {available.length === 0 ? (
        // An empty state that says what to do. "No applications" alone reads as a fault.
        <p>No applications are enabled yet. Your administrator can request access.</p>
      ) : (
        <ul>
          {available.map((app) => (
            <li key={app.id}>{app.name}</li>
          ))}
        </ul>
      )}

      {unlicensed > 0 && <p>{unlicensed} further application(s) are available to license.</p>}
    </main>
  )
}
