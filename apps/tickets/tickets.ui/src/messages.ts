/**
 * Wording per problem code.
 *
 * Keyed on the server's stable `problemCode` rather than its message, which is prose and will be
 * reworded. A code with no entry falls back to something honest rather than showing the raw code.
 */
export const TICKET_PROBLEMS: Record<string, string> = {
  validation_failed: 'Check the details and try again.',
  not_found: 'That ticket no longer exists. Reload the list.',
  already_closed: 'This ticket was already closed.',
  assignee_not_found: 'That person is not on the employee list.',
  assignee_terminated: 'That person has left. Assign someone who still works here.',
  concurrent_change: 'Someone else changed this ticket. Reload and try again.',
  app_not_licensed: 'Your organisation does not have the Tickets app enabled.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

export function explain(problemCode: string): string {
  return TICKET_PROBLEMS[problemCode] ?? 'Something went wrong. Please try again.'
}
