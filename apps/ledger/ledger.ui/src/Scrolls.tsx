import type { ReactNode } from 'react'

/**
 * Lets wide content — a table — scroll inside itself instead of widening the page. CLAUDE.md: wide
 * content never widens the document; checked at 390px by the ledger's browser journey.
 */
export function Scrolls({ children }: { children: ReactNode }) {
  return <div style={{ overflowX: 'auto', maxWidth: '100%' }}>{children}</div>
}
