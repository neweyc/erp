import { useState } from 'react'
import type { Period } from './api'

/**
 * How far the books are closed, and closing them further. Offered to everyone; the server decides
 * who may close (administrators) and says so plainly if the viewer may not — the shell hides, the
 * API enforces.
 */
export function PeriodSection({
  periods, onClose,
}: {
  periods: readonly Period[]
  onClose: (closedThrough: string) => Promise<boolean>
}) {
  const [through, setThrough] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    if (await onClose(through)) setThrough('')
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

      <form onSubmit={submit} aria-label="Close the books">
        <label htmlFor="close-through">Close through</label>
        <input id="close-through" type="date" value={through} onChange={(e) => setThrough(e.target.value)} required />
        {/* Said before the click: there is no reopen. */}
        <button type="submit" disabled={submitting || through === ''}>Close the books</button>
        <p>Closing is permanent. Corrections to a closed period are posted in an open one.</p>
      </form>
    </section>
  )
}
