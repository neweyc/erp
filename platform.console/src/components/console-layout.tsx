import { useState, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { LogOut } from 'lucide-react'
import { Alert, Button, cn } from '@app-platform/ui-kit'
import { api, ApiError, routes } from '../lib/api'
import { Brand } from './brand'
import { useSession } from './session'

const NAV = [
  { to: '/tenants', label: 'Tenants' },
  { to: '/errors', label: 'Errors' },
  { to: '/audit', label: 'Audit log' },
]

/**
 * The frame around every signed-in page: the console's name, its three sections, and who is signed
 * in. A top bar rather than the shell's sidebar, since there are three places to go.
 */
export function ConsoleLayout({ children }: { children: ReactNode }) {
  const { session, signedOut } = useSession()
  const [signingOut, setSigningOut] = useState(false)
  const [signOutFailed, setSignOutFailed] = useState<string | null>(null)

  /**
   * Says "signed out" only once the server has revoked the session. Leaving the page on a failed
   * request would look like a sign-out while the cookie still works: a refresh would restore access,
   * and a copied cookie would keep working until the session times out.
   */
  async function signOut() {
    setSigningOut(true)
    setSignOutFailed(null)
    try {
      await api(routes.signOut, { method: 'POST' })
      signedOut('signed_out')
    } catch (caught) {
      // A 401 means the session had already ended, which is what was asked for.
      if (caught instanceof ApiError && caught.status === 401) signedOut('signed_out')
      else setSignOutFailed(caught instanceof ApiError ? caught.problemCode : 'unknown')
      setSigningOut(false)
    }
  }

  return (
    <div className="min-h-svh">
      <header className="border-b bg-card">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3 sm:px-6">
          <Brand />
          <nav aria-label="Console" className="flex flex-wrap items-center gap-1">
            {NAV.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                className={({ isActive }) =>
                  cn(
                    'rounded-md px-3 py-1.5 text-sm font-medium text-muted-foreground hover:bg-accent hover:text-accent-foreground',
                    isActive && 'bg-accent text-accent-foreground',
                  )
                }
              >
                {item.label}
              </NavLink>
            ))}
          </nav>
          <div className="ml-auto flex min-w-0 items-center gap-3">
            <span className="truncate text-sm text-muted-foreground">{session?.email}</span>
            <Button size="sm" variant="ghost" onClick={() => void signOut()} disabled={signingOut}>
              <LogOut aria-hidden />
              Sign out
            </Button>
          </div>
        </div>
      </header>
      <main className="mx-auto min-w-0 max-w-6xl px-4 py-8 sm:px-6">
        {signOutFailed && (
          <Alert tone="danger" className="mb-6">
            {signOutFailed === 'csrf_failed'
              ? 'Sign-out was refused because this browser has lost its security token, so this session is still active. On localhost, signing out of the app clears it: see docs/local-development.md.'
              : 'Sign-out could not be confirmed, so this session may still be active. Try again.'}
          </Alert>
        )}
        {children}
      </main>
    </div>
  )
}
