import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { Loading } from '@app-platform/ui-kit'
import { ConsoleLayout } from './components/console-layout'
import { SessionProvider, useSession } from './components/session'
import { AuditLog } from './pages/audit-log'
import { ErrorFeed } from './pages/error-feed'
import { SignIn } from './pages/sign-in'
import { TenantPage } from './pages/tenant'
import { Tenants } from './pages/tenants'

export function ConsoleRoutes() {
  const { session, loading } = useSession()

  if (loading) return <Loading className="min-h-svh justify-center">Loading…</Loading>
  if (!session) return <SignIn />

  return (
    <ConsoleLayout>
      <Routes>
        <Route path="/tenants" element={<Tenants />} />
        <Route path="/tenants/:tenantId" element={<TenantPage />} />
        <Route path="/errors" element={<ErrorFeed />} />
        <Route path="/audit" element={<AuditLog />} />
        <Route path="*" element={<Navigate to="/tenants" replace />} />
      </Routes>
    </ConsoleLayout>
  )
}

export function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <ConsoleRoutes />
      </SessionProvider>
    </BrowserRouter>
  )
}
