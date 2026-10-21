import { useCallback, useEffect, useRef, useState } from 'react'
import { CircleCheck, Inbox } from 'lucide-react'
import { ApiError, Permissions, type AppProps } from '@app-platform/web-api'
import {
  Alert, Badge, Button, Card, CardContent, Checkbox, EmptyState, Input, Label, Loading, NativeSelect, PageHeader,
  PageStack,
} from '@app-platform/ui-kit'
import {
  assignTicket, closeTicket, createTicket, listEmployees, listTickets,
  type Employee, type Ticket,
} from './api'
import { explain } from './messages'

/**
 * The tickets app.
 *
 * One page on purpose: list, create, assign, close. A ticket's whole lifecycle is visible without
 * navigating, which is what makes the workflow testable end to end and what a user of a small
 * queue actually wants.
 *
 * Actions the user's role does not allow are hidden, not disabled (a requester raises tickets but
 * does not assign or close them). The API refuses them regardless; this only keeps the page honest.
 */
export default function TicketsApp({ permissions }: AppProps) {
  const canCreate = permissions.includes(Permissions.tickets.ticketCreate)
  const canAssign = permissions.includes(Permissions.tickets.ticketAssign)
  const canClose = permissions.includes(Permissions.tickets.ticketClose)

  const [tickets, setTickets] = useState<Ticket[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [includeClosed, setIncludeClosed] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  /**
   * Identifies the most recent ticket request. A response from an earlier one is discarded.
   *
   * Without this, ticking "show closed" while a mutation's reload was still in flight could leave
   * the checkbox on and the closed tickets absent, with nothing reloading until the next click —
   * a list contradicting its own control.
   */
  const latestRequest = useRef(0)

  const refresh = useCallback(async (closed: boolean) => {
    const request = ++latestRequest.current
    setLoading(true)

    try {
      const ticketRows = await listTickets(closed)

      if (request !== latestRequest.current) return

      setTickets(ticketRows)
      setError(null)
    } catch (caught) {
      if (request !== latestRequest.current) return

      setError(caught instanceof ApiError ? explain(caught.problemCode) : 'Could not load tickets.')
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh(includeClosed)
  }, [refresh, includeClosed])

  // The roster is loaded once, not on every reload. It does not change when the ticket filter does,
  // and refetching it on each mutation meant closing one ticket re-downloaded every employee.
  useEffect(() => {
    listEmployees()
      .then(setEmployees)
      .catch(() => {
        // A failed roster leaves assignment impossible but the list still readable, so this does
        // not become the page's error — the picker being empty is the visible consequence.
      })
  }, [])

  /**
   * Runs a mutation, then reloads from the server rather than patching local state.
   *
   * The reload happens on failure too: a rejected close may mean somebody else closed it, and the
   * list should show what is actually true rather than what was attempted.
   *
   * The message is set AFTER that reload, not before. `refresh` clears the error on success, so
   * setting it first meant every failure message was wiped by the reload that followed it — the
   * user saw nothing at all.
   */
  /// <returns>True when the action succeeded, so a caller can keep the user's input on failure.</returns>
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setError(null)

    let failure: string | null = null

    try {
      await action()
    } catch (caught) {
      failure = caught instanceof ApiError ? explain(caught.problemCode) : 'Something went wrong.'
    }

    await refresh(includeClosed)

    if (failure === null) return true

    setError(failure)
    return false
  }

  const openCount = tickets.filter((t) => t.status === 'Open').length

  return (
    <PageStack>
      <PageHeader title="Tickets" description="Raise a request for help, give it to someone, and close it when it is done." />

      {error && <Alert tone="danger">{error}</Alert>}

      {canCreate && <NewTicketForm onCreate={(title) => run(() => createTicket(title))} />}

      <Card>
        <div className="flex flex-wrap items-center justify-between gap-3 border-b px-5 py-3 sm:px-6">
          <p className="text-sm font-medium">
            {loading ? 'Tickets' : `${openCount} open${includeClosed ? `, ${tickets.length - openCount} closed` : ''}`}
          </p>
          <label className="flex cursor-pointer items-center gap-2 text-sm text-muted-foreground">
            <Checkbox checked={includeClosed} onChange={(e) => setIncludeClosed(e.target.checked)} />
            Show closed tickets
          </label>
        </div>

        {loading ? (
          <Loading className="px-6">Loading tickets…</Loading>
        ) : tickets.length === 0 ? (
          // Says what to do rather than just reporting emptiness, which reads as a fault.
          <EmptyState icon={Inbox} title={canCreate ? 'No tickets yet. Raise the first one above.' : 'No tickets yet.'} />
        ) : (
          <ul aria-label="Tickets" className="divide-y">
            {tickets.map((ticket) => (
              <li key={ticket.ticketId}>
                <TicketRow
                  ticket={ticket}
                  employees={employees}
                  onAssign={canAssign ? (employeeId) => run(() => assignTicket(ticket.ticketId, employeeId)) : null}
                  onClose={canClose ? () => run(() => closeTicket(ticket.ticketId)) : null}
                />
              </li>
            ))}
          </ul>
        )}
      </Card>
    </PageStack>
  )
}

function NewTicketForm({ onCreate }: { onCreate: (title: string) => Promise<boolean> }) {
  const [title, setTitle] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)

    // Cleared only on success. Clearing unconditionally threw away what the person typed at the
    // same moment the page told them to check it and try again.
    if (await onCreate(title)) setTitle('')

    setSubmitting(false)
  }

  return (
    <Card>
      <CardContent>
        <form onSubmit={submit} className="grid gap-2">
          <Label htmlFor="ticket-title">New ticket</Label>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Input
              id="ticket-title"
              placeholder="What needs doing?"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              required
              maxLength={200}
            />
            <Button type="submit" variant="primary" disabled={submitting || title.trim().length === 0} className="sm:w-36">
              {submitting ? 'Raising…' : 'Raise ticket'}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

function TicketRow({
  ticket, employees, onAssign, onClose,
}: {
  ticket: Ticket
  employees: Employee[]
  /** Null when the user may not assign: the picker is not shown at all. */
  onAssign: ((employeeId: string | null) => Promise<boolean>) | null
  /** Null when the user may not close. */
  onClose: (() => Promise<boolean>) | null
}) {
  const closed = ticket.status === 'Closed'

  return (
    <article className="flex flex-col gap-3 px-5 py-4 sm:px-6 lg:flex-row lg:items-center">
      <div className="grid min-w-0 flex-1 gap-1.5">
        <div className="flex min-w-0 items-start gap-2.5 lg:items-center">
          <Badge tone={closed ? 'neutral' : 'info'} dot>
            {closed ? 'Closed' : 'Open'}
          </Badge>
          {/* Wraps on a phone, where a cut-off title would hide what the ticket is; one line on a wide screen. */}
          <h2 className={closed ? 'min-w-0 text-sm font-medium break-words text-muted-foreground line-through decoration-muted-foreground/40 lg:truncate' : 'min-w-0 text-sm font-medium break-words lg:truncate'}>
            {ticket.title}
          </h2>
        </div>
        <p className="text-xs text-muted-foreground">
          Assigned to:{' '}
          {/* The stored snapshot, which still names the assignee after that employee is removed
              from core. Rendering a live lookup here would show a blank instead. */}
          <span className="font-medium text-foreground">{ticket.assigneeDisplayName ?? 'Nobody'}</span>
          {/* Beside the name it undoes, so the controls on the right line up row to row. */}
          {onAssign && !closed && ticket.assigneeDisplayName !== null && (
            <>
              {' · '}
              <Button size="sm" variant="link" className="h-auto p-0 text-xs" onClick={() => void onAssign(null)}>
                Unassign
              </Button>
            </>
          )}
        </p>
      </div>

      {(onAssign || (!closed && onClose)) && (
        <div className="flex items-center gap-2">
          {onAssign && (
            <>
              <Label htmlFor={`assignee-${ticket.ticketId}`} className="sr-only">
                Assignee
              </Label>
              <NativeSelect
                id={`assignee-${ticket.ticketId}`}
                className="flex-1 lg:w-48 lg:flex-none"
                // Pick-only, never free text: a typed name cannot become an assignment, so a mistyped
                // person dead-ends rather than being recorded.
                //
                // Held at "" so the control always reads "Assign to…" rather than claiming to show
                // current state — the assignee is displayed beside it, from the stored snapshot.
                // Unassigning therefore needs its own action: selecting the placeholder cannot fire a
                // change event.
                value=""
                disabled={closed}
                onChange={(e) => void onAssign(e.target.value)}
              >
                <option value="">Assign to…</option>
                {employees.map((employee) => (
                  <option key={employee.employeeId} value={employee.employeeId}>
                    {employee.displayName}
                  </option>
                ))}
              </NativeSelect>

            </>
          )}

          {!closed && onClose && (
            <Button size="sm" onClick={() => void onClose()}>
              <CircleCheck /> Close ticket
            </Button>
          )}
        </div>
      )}
    </article>
  )
}
