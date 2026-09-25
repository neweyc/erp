import { api } from '@app-platform/web-api'

export interface Ticket {
  readonly ticketId: string
  readonly title: string
  readonly status: 'Open' | 'Closed'
  readonly assigneeDisplayName: string | null
  readonly createdAt: string
  readonly closedAt: string | null
}

/** From core's published contract, not from tickets' own tables. */
export interface Employee {
  readonly employeeId: string
  readonly displayName: string
  readonly status: string
}

export function listTickets(includeClosed: boolean): Promise<Ticket[]> {
  return api<Ticket[]>(`/api/tickets/v1/tickets?includeClosed=${includeClosed}`)
}

export function createTicket(title: string, description?: string): Promise<{ ticketId: string }> {
  return api('/api/tickets/v1/tickets', {
    method: 'POST',
    body: JSON.stringify({ title, description }),
  })
}

export function assignTicket(ticketId: string, employeeId: string | null): Promise<unknown> {
  return api(`/api/tickets/v1/tickets/${ticketId}/assignee`, {
    method: 'POST',
    body: JSON.stringify({ employeeId }),
  })
}

export function closeTicket(ticketId: string): Promise<unknown> {
  return api(`/api/tickets/v1/tickets/${ticketId}/close`, { method: 'POST' })
}

/**
 * Employees come from CORE, not from the tickets API.
 *
 * Tickets stores an employee id and a display snapshot; it does not own the roster. The picker
 * therefore asks the service that does, and the snapshot is what keeps a closed ticket readable
 * after an employee is gone.
 */
export function listEmployees(): Promise<Employee[]> {
  return api<Employee[]>('/api/core/v1/employees')
}
