import { useState } from 'react'
import { CalendarCheck, Lock } from 'lucide-react'
import { Button, Card, CardContent, CardHeader, Field, Input } from '@app-platform/ui-kit'
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
    <Card>
      <section aria-labelledby="period-heading">
        <CardHeader id="period-heading" title="Closed periods" description="Nothing can be posted on or before a closed date." />
        <CardContent className="grid gap-4">
          <ul className="grid gap-2">
            {periods.map((p) => (
              <li key={p.companyId} className="flex items-center gap-3 rounded-lg border bg-muted/30 px-3 py-2.5 text-sm">
                {p.closedThrough ? (
                  <Lock className="size-4 shrink-0 text-muted-foreground" />
                ) : (
                  <CalendarCheck className="size-4 shrink-0 text-muted-foreground" />
                )}
                <span className="min-w-0">
                  <span className="font-medium">{p.companyName}</span>:{' '}
                  <span className="whitespace-nowrap">
                    {p.closedThrough ? `closed through ${p.closedThrough}` : 'no period closed yet'}
                  </span>
                </span>
              </li>
            ))}
          </ul>

          {onClose && (
            <form onSubmit={submit} aria-label="Close the books" className="grid gap-3 border-t pt-4">
              <div className="flex flex-wrap items-end gap-2">
                <Field label="Close through" htmlFor="close-through" className="min-w-40 flex-1">
                  <Input id="close-through" type="date" value={through} onChange={(e) => setThrough(e.target.value)} required />
                </Field>
                <Button type="submit" disabled={submitting || through === ''}>
                  <Lock /> Close the books
                </Button>
              </div>
              {/* Said before the click: there is no reopen. */}
              <p className="text-xs text-muted-foreground">
                Closing is permanent. Corrections to a closed period are posted in an open one.
              </p>
            </form>
          )}
        </CardContent>
      </section>
    </Card>
  )
}
