import { Suspense } from 'react'
import { Link, Navigate, Route, Routes } from 'react-router-dom'
import { Compass } from 'lucide-react'
import { Button, Card, EmptyState, Loading } from '@app-platform/ui-kit'
import { APPS, componentFor, visibleApps } from '../lib/apps'
import { Permissions } from '@app-platform/web-api'
import { useSession } from './session-provider'
import { Dashboard } from '../pages/dashboard'
import { Employees } from '../pages/employees'
import { Roles } from '../pages/roles'
import { Users } from '../pages/users'

/**
 * Routes built FROM the registry. An app the viewer may not open has no route at all — not a
 * route that renders "no access", which would still have fetched the bundle to find out.
 */
export function AppRoutes() {
  const { session } = useSession()

  if (!session) return null

  const available = visibleApps(session.licensedApps, session.permissions)

  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/dashboard" element={<Dashboard />} />
      {session.permissions.includes(Permissions.core.employeeRead) && (
        <Route path="/employees" element={<Employees />} />
      )}
      {session.permissions.includes(Permissions.core.userRead) && (
        <>
          <Route path="/users" element={<Users />} />
          <Route path="/roles" element={<Roles />} />
        </>
      )}

      {available.map((app) => {
        const Component = componentFor(app)

        return (
          <Route
            key={app.id}
            path={`${app.path}/*`}
            element={
              // The fallback is what the reader sees while the app's bundle downloads. It is
              // deliberately plain: a spinner that appears for 80ms on a fast connection reads
              // as a flicker, not as progress.
              <Suspense fallback={<Loading>Loading {app.name}…</Loading>}>
                <Component permissions={session.permissions} />
              </Suspense>
            }
          />
        )
      })}

      <Route path="*" element={<NotFound licensedCount={available.length} />} />
    </Routes>
  )
}

function NotFound({ licensedCount }: { licensedCount: number }) {
  return (
    <Card>
      <EmptyState
        icon={Compass}
        title="Page not found"
        headingLevel={1}
        action={
          <Button asChild variant="outline" size="sm">
            <Link to="/dashboard">Back to the dashboard</Link>
          </Button>
        }
      >
        {licensedCount < APPS.length
          ? // Said plainly rather than left as a bare 404: a reader who was sent a link to an app
            // their organization has not licensed should learn that, not conclude the link is broken.
            'Some applications are not enabled for your organization.'
          : 'There is nothing at this address.'}
      </EmptyState>
    </Card>
  )
}
