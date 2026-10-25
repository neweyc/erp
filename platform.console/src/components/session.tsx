import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { api, ApiError, routes } from '../lib/api'

export interface OperatorSession {
  readonly email: string
  /** Where tenant addresses sit (https://app.example.com), to show an address before it exists. */
  readonly tenantBaseUrl: string
}

interface SessionState {
  readonly session: OperatorSession | null
  readonly loading: boolean
  readonly refresh: () => Promise<void>
  /** Called when the server says the session has ended, with its reason, so sign-in can say why. */
  readonly signedOut: (problemCode: string) => void
  readonly problemCode: string | null
}

const SessionContext = createContext<SessionState | null>(null)

/**
 * Who is signed in to the console, asked of platform.api when the page opens. Shaped like the
 * shell's provider on purpose; it is a separate one because operators are not customers and share
 * no session, cookie or code path with them.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<OperatorSession | null>(null)
  const [loading, setLoading] = useState(true)
  const [problemCode, setProblemCode] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      setSession(await api<OperatorSession>(routes.session))
      setProblemCode(null)
    } catch (error) {
      setSession(null)
      setProblemCode(error instanceof ApiError ? error.problemCode : 'unknown')
    } finally {
      setLoading(false)
    }
  }, [])

  const signedOut = useCallback((code: string) => {
    setSession(null)
    setProblemCode(code)
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <SessionContext.Provider value={{ session, loading, refresh, signedOut, problemCode }}>
      {children}
    </SessionContext.Provider>
  )
}

export function useSession(): SessionState {
  const context = useContext(SessionContext)
  if (!context) throw new Error('useSession must be used inside a SessionProvider.')
  return context
}
