import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError, fetchSession, type Session } from '../lib/session'

interface SessionState {
  readonly session: Session | null
  readonly loading: boolean
  readonly refresh: () => Promise<void>
  readonly signedOut: (problemCode: string) => void
  readonly problemCode: string | null
}

const SessionContext = createContext<SessionState | null>(null)

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null)
  const [loading, setLoading] = useState(true)
  const [problemCode, setProblemCode] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      setSession(await fetchSession())
      setProblemCode(null)
    } catch (error) {
      setSession(null)
      // Carried through so the sign-in page can say WHICH thing happened. A generic "please
      // sign in" cannot distinguish an expired session from a revoked one from an
      // organisation on hold, and each needs a different next step from the reader.
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

  if (!context) {
    // A hook used outside its provider renders undefined rather than failing, and the bug
    // surfaces later as a blank screen somewhere unrelated.
    throw new Error('useSession must be used inside a SessionProvider.')
  }

  return context
}
