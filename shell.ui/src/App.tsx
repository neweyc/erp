import { BrowserRouter } from 'react-router-dom'
import { AppRoutes } from './components/app-routes'
import { AppShell } from './components/app-shell'
import { SessionProvider, useSession } from './components/session-provider'
import { SignIn } from './pages/sign-in'

function Authenticated() {
  const { session, loading } = useSession()

  if (loading) return <p role="status">Loading…</p>
  if (!session) return <SignIn />

  return (
    <BrowserRouter>
      <AppShell>
        <AppRoutes />
      </AppShell>
    </BrowserRouter>
  )
}

export function App() {
  // The session provider sits ABOVE the router, so the signed-out routes are covered too and
  // the app does not have to be inside a route to know who is looking at it.
  return (
    <SessionProvider>
      <Authenticated />
    </SessionProvider>
  )
}
