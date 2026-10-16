import { useState } from 'react'
import type { Period } from './api'

/**
 * How far the books are closed, and closing them further. The close form is shown only to a
 * controller (ledger.period.close); everyone who can read the ledger sees the closed periods. The
 * API enforces the permission regardless.
 */
export function PeriodSection({
  periods, onClose,
}: {
  periods: readonly Period[]
  /** Null when the user may not close periods: the form is not shown. */
  onClose: ((closedThrough: string) => Promise<boolean>) | null
}) {
  const [through, setThrough] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    if (onClose && (await onClose(through))) setThrough('')
    setSubmitting(false)
  }

  return (
    <section aria-labelledby="period-heading">
      <h2 id="period-heading">Closed periods</h2>

      {periods.map((p) => (
        <p key={p.companyId}>
          {p.companyName}: {p.closedThrough ? `closed through ${p.closedThrough}` : 'no period closed yet'}
        </p>
      ))}

      {onClose && (
        <form onSubmit={submit} aria-label="Close the books">
          <label htmlFor="close-through">Close through</label>
          <input id="close-through" type="date" value={through} onChange={(e) => setThrough(e.target.value)} required />
          {/* Said before the click: there is no reopen. */}
          <button type="submit" disabled={submitting || through === ''}>Close the books</button>
          <p>Closing is permanent. Corrections to a closed period are posted in an open one.</p>
        </form>
      )}
    </section>
  )
}
