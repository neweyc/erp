import { useCallback, useEffect, useRef, useState } from 'react'
import { useSession } from '../components/session'
import { api, ApiError } from './api'

export interface Loaded<T> {
  readonly data: T | null
  /** The stable problem code when the read failed. */
  readonly error: string | null
  /**
   * When the data on screen was read. Every console screen shows it (platform.console/CLAUDE.md):
   * operator screens are trusted to be live, and they quietly go stale.
   */
  readonly readAt: Date | null
  readonly reload: () => Promise<void>
}

/**
 * Reads one route when the page opens, and again whenever `reload` is called. A 401 means the
 * operator's session has ended (30 minutes idle, eight hours in all, or revoked), so it returns
 * them to sign-in with the reason rather than showing a page of errors.
 *
 * Only the newest read is shown. Reads can finish out of order: a slow Refresh started before a
 * suspension could otherwise land after the reload that follows it, putting "Active" back on screen
 * with the newest "Read at" time. A reload left over from a page the operator has since moved away
 * from (a change on tenant A finishing after they opened tenant B) reads nothing at all.
 */
export function useLoad<T>(path: string): Loaded<T> {
  const { signedOut } = useSession()
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [readAt, setReadAt] = useState<Date | null>(null)
  const latest = useRef(0)
  const currentPath = useRef(path)

  const reload = useCallback(async () => {
    if (path !== currentPath.current) return
    const read = ++latest.current
    try {
      const loaded = await api<T>(path)
      if (read !== latest.current) return
      setData(loaded)
      setError(null)
      setReadAt(new Date())
    } catch (caught) {
      if (read !== latest.current) return
      const code = caught instanceof ApiError ? caught.problemCode : 'unknown'
      if (caught instanceof ApiError && caught.status === 401) signedOut(code)
      else setError(code)
    }
  }, [path, signedOut])

  // A different route is a different page's data: clear the old one rather than show it as current.
  useEffect(() => {
    currentPath.current = path
    setData(null)
    setError(null)
    setReadAt(null)
    void reload()
    // Whatever is still in flight belongs to the page being left.
    return () => {
      latest.current++
    }
  }, [path, reload])

  return { data, error, readAt, reload }
}
