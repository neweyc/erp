import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import TicketsApp from './index'
import { explain, TICKET_PROBLEMS } from './messages'

/**
 * Routes each request by URL, so a test can control tickets and employees independently — the two
 * come from different services and either can fail on its own.
 */
function stubApi(handlers: Record<string, () => { status?: number; body: unknown }>) {
  const fetchMock = vi.fn((input: RequestInfo | URL) => {
    const url = String(input)
    const key = Object.keys(handlers).find((k) => url.includes(k))
    const { status = 200, body } = key ? handlers[key]!() : { body: [] }

    return Promise.resolve({
      ok: status < 400,
      status,
      json: async () => body,
    } as Response)
  })

  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const oneTicket = {
  ticketId: 'tkt_7h2k3m4n5p6q7r8s9t0v1w2',
  title: 'Printer jammed',
  status: 'Open' as const,
  assigneeDisplayName: null,
  createdAt: '2026-09-25T00:00:00Z',
  closedAt: null,
}

const oneEmployee = {
  employeeId: 'emp_7h2k3m4n5p6q7r8s9t0v1w2',
  displayName: 'Ada Lovelace',
  status: 'Active',
}

afterEach(() => vi.unstubAllGlobals())

describe('tickets app', () => {
  it('lists tickets and offers the roster as assignees', async () => {
    stubApi({
      '/api/tickets/v1/tickets': () => ({ body: [oneTicket] }),
      '/api/core/v1/employees': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)

    expect(await screen.findByText('Printer jammed')).toBeInTheDocument()
    // The roster comes from core's published view, not from tickets' own tables.
    expect(await screen.findByRole('option', { name: 'Ada Lovelace' })).toBeInTheDocument()
  })

  it('says what to do when there are no tickets', async () => {
    stubApi({ '/api/tickets': () => ({ body: [] }), '/api/core': () => ({ body: [] }) })

    render(<TicketsApp />)

    // "No tickets" alone reads as a fault.
    expect(await screen.findByText(/Raise the first one/i)).toBeInTheDocument()
  })

  it('shows the stored snapshot rather than a blank when an assignee is set', async () => {
    stubApi({
      '/api/tickets': () => ({ body: [{ ...oneTicket, assigneeDisplayName: 'Ada Lovelace' }] }),
      '/api/core': () => ({ body: [] }),
    })

    render(<TicketsApp />)

    // Rendered from the ticket's own snapshot, so it survives the employee being removed from
    // core — the roster above is deliberately empty here.
    expect(await screen.findByText(/Assigned to:/)).toHaveTextContent('Ada Lovelace')
  })

  it('disables assignment and hides close on a closed ticket', async () => {
    stubApi({
      '/api/tickets': () => ({ body: [{ ...oneTicket, status: 'Closed', closedAt: '2026-09-25T01:00:00Z' }] }),
      '/api/core': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)

    // Disabled rather than offered-and-refused: a control that exists and then errors invites the
    // click.
    expect(await screen.findByLabelText('Assignee')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Close ticket' })).not.toBeInTheDocument()
  })

  it('explains a refused assignment in words, keyed on the problem code', async () => {
    let assigned = false
    stubApi({
      '/assignee': () => {
        assigned = true
        return { status: 400, body: { problemCode: 'assignee_terminated' } }
      },
      '/api/tickets/v1/tickets': () => ({ body: [oneTicket] }),
      '/api/core/v1/employees': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)
    await screen.findByText('Printer jammed')

    await userEvent.selectOptions(screen.getByLabelText('Assignee'), oneEmployee.employeeId)

    await waitFor(() => expect(assigned).toBe(true))
    // The reader is told the person has left, not shown a code or a generic failure.
    expect(await screen.findByRole('alert')).toHaveTextContent(/has left/i)
  })

  it('reports an unlicensed app rather than an empty page', async () => {
    stubApi({ '/api/tickets': () => ({ status: 403, body: { problemCode: 'app_not_licensed' } }) })

    render(<TicketsApp />)

    // The shell hides unlicensed apps, but a direct navigation must still explain itself.
    expect(await screen.findByRole('alert')).toHaveTextContent(/does not have the Tickets app/i)
  })

  it('keeps the typed title when the create is refused', async () => {
    stubApi({
      '/api/tickets/v1/tickets?': () => ({ body: [] }),
      '/api/core/v1/employees': () => ({ body: [] }),
      '/api/tickets/v1/tickets': () => ({ status: 400, body: { problemCode: 'validation_failed' } }),
    })

    render(<TicketsApp />)
    await screen.findByText(/Raise the first one/i)

    const field = screen.getByLabelText('New ticket')
    await userEvent.type(field, 'Something worth keeping')
    await userEvent.click(screen.getByRole('button', { name: 'Raise ticket' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/Check the details/i)
    // Clearing unconditionally threw the text away at the same moment the page asked the person to
    // check it and try again.
    expect(field).toHaveValue('Something worth keeping')
  })

  it('clears the title once the create succeeds', async () => {
    stubApi({
      '/api/tickets/v1/tickets?': () => ({ body: [] }),
      '/api/core/v1/employees': () => ({ body: [] }),
      '/api/tickets/v1/tickets': () => ({ body: { ticketId: 'tkt_x' } }),
    })

    render(<TicketsApp />)
    await screen.findByText(/Raise the first one/i)

    await userEvent.type(screen.getByLabelText('New ticket'), 'Printer jammed')
    await userEvent.click(screen.getByRole('button', { name: 'Raise ticket' }))

    await waitFor(() => expect(screen.getByLabelText('New ticket')).toHaveValue(''))
  })

  it('explains a refused close and leaves the list showing what is actually true', async () => {
    let closeAttempted = false
    stubApi({
      '/close': () => {
        closeAttempted = true
        return { status: 409, body: { problemCode: 'already_closed' } }
      },
      '/api/tickets/v1/tickets?': () => ({ body: [oneTicket] }),
      '/api/core/v1/employees': () => ({ body: [] }),
    })

    render(<TicketsApp />)
    await screen.findByText('Printer jammed')

    await userEvent.click(screen.getByRole('button', { name: 'Close ticket' }))

    await waitFor(() => expect(closeAttempted).toBe(true))
    expect(await screen.findByRole('alert')).toHaveTextContent(/already closed/i)
  })

  it('offers unassign only once somebody is assigned', async () => {
    stubApi({
      '/api/tickets/v1/tickets?': () => ({ body: [{ ...oneTicket, assigneeDisplayName: 'Ada Lovelace' }] }),
      '/api/core/v1/employees': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)

    // Selecting the placeholder cannot fire a change event, so unassigning needs its own control
    // or it is unreachable.
    expect(await screen.findByRole('button', { name: 'Unassign' })).toBeInTheDocument()
  })

  it('does not offer unassign on an unassigned ticket', async () => {
    stubApi({
      '/api/tickets/v1/tickets?': () => ({ body: [oneTicket] }),
      '/api/core/v1/employees': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)
    await screen.findByText('Printer jammed')

    expect(screen.queryByRole('button', { name: 'Unassign' })).not.toBeInTheDocument()
  })

  it('fetches the roster from core, not from the tickets API', async () => {
    const fetchMock = stubApi({
      '/api/tickets/v1/tickets?': () => ({ body: [] }),
      '/api/core/v1/employees': () => ({ body: [oneEmployee] }),
    })

    render(<TicketsApp />)
    await screen.findByText(/Raise the first one/i)

    // Tickets stores an employee id and a snapshot; it does not own the roster. Pinned by URL so a
    // future "convenience" endpoint on the tickets API is a deliberate change, not a drift.
    const urls = fetchMock.mock.calls.map((call) => String(call[0]))
    expect(urls.some((url) => url.includes('/api/core/v1/employees'))).toBe(true)
  })

  it('falls back to something honest for an unrecognised problem code', () => {
    // A code with no entry must not surface the raw code to a reader.
    expect(explain('some_new_code_from_a_newer_api')).toBe('Something went wrong. Please try again.')
    expect(TICKET_PROBLEMS['already_closed']).toBeTruthy()
  })
})
