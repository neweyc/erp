import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '@app-platform/web-api'
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
 */
export default function TicketsApp() {
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

  return (
    <main>
      <h1>Tickets</h1>

      {error && <p role="alert">{error}</p>}

      <NewTicketForm onCreate={(title) => run(() => createTicket(title))} />

      <label>
        <input
          type="checkbox"
          checked={includeClosed}
          onChange={(e) => setIncludeClosed(e.target.checked)}
        />
        Show closed tickets
      </label>

      {loading ? (
        <p role="status">Loading tickets…</p>
      ) : tickets.length === 0 ? (
        // Says what to do rather than just reporting emptiness, which reads as a fault.
        <p>No tickets yet. Raise the first one above.</p>
      ) : (
        <ul aria-label="Tickets">
          {tickets.map((ticket) => (
            <li key={ticket.ticketId}>
              <TicketRow
                ticket={ticket}
                employees={employees}
                onAssign={(employeeId) => run(() => assignTicket(ticket.ticketId, employeeId))}
                onClose={() => run(() => closeTicket(ticket.ticketId))}
              />
            </li>
          ))}
        </ul>
      )}
    </main>
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
    <form onSubmit={submit}>
      <label htmlFor="ticket-title">New ticket</label>
      <input
        id="ticket-title"
        value={title}
        onChange={(e) => setTitle(e.target.value)}
        required
        maxLength={200}
      />
      <button type="submit" disabled={submitting || title.trim().length === 0}>
        {submitting ? 'Raising…' : 'Raise ticket'}
      </button>
    </form>
  )
}

function TicketRow({
  ticket, employees, onAssign, onClose,
}: {
  ticket: Ticket
  employees: Employee[]
  onAssign: (employeeId: string | null) => Promise<boolean>
  onClose: () => Promise<boolean>
}) {
  const closed = ticket.status === 'Closed'

  return (
    <article>
      <h2>{ticket.title}</h2>
      <p>{closed ? 'Closed' : 'Open'}</p>

      <p>
        Assigned to:{' '}
        {/* The stored snapshot, which still names the assignee after that employee is removed
            from core. Rendering a live lookup here would show a blank instead. */}
        <span>{ticket.assigneeDisplayName ?? 'Nobody'}</span>
      </p>

      <label htmlFor={`assignee-${ticket.ticketId}`}>Assignee</label>
      <select
        id={`assignee-${ticket.ticketId}`}
        // Pick-only, never free text: a typed name cannot become an assignment, so a mistyped
        // person dead-ends rather than being recorded.
        //
        // Held at "" so the control always reads "Choose…" rather than claiming to show current
        // state — the assignee is displayed above, from the stored snapshot. Unassigning therefore
        // needs its own action: selecting the placeholder cannot fire a change event.
        value=""
        disabled={closed}
        onChange={(e) => void onAssign(e.target.value)}
      >
        <option value="">Choose…</option>
        {employees.map((employee) => (
          <option key={employee.employeeId} value={employee.employeeId}>
            {employee.displayName}
          </option>
        ))}
      </select>

      {!closed && ticket.assigneeDisplayName !== null && (
        <button type="button" onClick={() => void onAssign(null)}>
          Unassign
        </button>
      )}

      {!closed && (
        <button type="button" onClick={() => void onClose()}>
          Close ticket
        </button>
      )}
    </article>
  )
}
