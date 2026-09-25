import { Suspense } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { APPS, componentFor, visibleApps } from '../lib/apps'
import { useSession } from './session-provider'
import { Dashboard } from '../pages/dashboard'

/**
 * Routes built FROM the registry. An app the viewer may not open has no route at all — not a
 * route that renders "no access", which would still have fetched the bundle to find out.
 */
export function AppRoutes() {
  const { session } = useSession()

  if (!session) return null

  const available = visibleApps(session.licensedApps, session.role)

  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/dashboard" element={<Dashboard />} />

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
              <Suspense fallback={<p role="status">Loading {app.name}…</p>}>
                <Component />
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
    <section>
      <h1>Page not found</h1>
      {licensedCount < APPS.length && (
        // Said plainly rather than left as a bare 404: a reader who was sent a link to an app
        // their organisation has not licensed should learn that, not conclude the link is broken.
        <p>Some applications are not enabled for your organisation.</p>
      )}
    </section>
  )
}
