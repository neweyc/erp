import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { AppRoutes } from './components/app-routes'
import { AppShell } from './components/app-shell'
import { SessionProvider, useSession } from './components/session-provider'
import { AcceptInvite } from './pages/accept-invite'
import { SignIn } from './pages/sign-in'

/**
 * Pages reachable without a session. They are matched BEFORE the session check, so someone
 * following an invitation link is not diverted to a sign-in form for an account they do not
 * have yet.
 */
function SignedOutRoutes() {
  return (
    <Routes>
      <Route path="/accept-invite" element={<AcceptInvite />} />
      <Route path="*" element={<SignIn />} />
    </Routes>
  )
}

function Authenticated() {
  const { session, loading } = useSession()

  if (loading) return <p role="status">Loading…</p>
  if (!session) return <SignedOutRoutes />

  return (
    <AppShell>
      <AppRoutes />
    </AppShell>
  )
}

export function App() {
  // The router wraps everything so signed-out routes exist; the session provider sits inside it
  // but above the app, so every page can ask who is looking at it.
  return (
    <BrowserRouter>
      <SessionProvider>
        <Authenticated />
      </SessionProvider>
    </BrowserRouter>
  )
}
