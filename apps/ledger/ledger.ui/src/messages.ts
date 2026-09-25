/**
 * Wording per problem code — keyed on the server's stable `problemCode`, never its message, which is
 * prose and will be reworded. A code with no entry falls back to something honest.
 */
export const LEDGER_PROBLEMS: Record<string, string> = {
  validation_failed: 'Check the details and try again.',
  not_found: 'That entry no longer exists. Reload the page.',
  company_not_found: 'Your organisation has no company set up.',
  company_required: 'Choose which company’s books to use.',
  duplicate_account_code: 'An account with that code already exists.',
  account_not_found: 'One of the accounts no longer exists. Reload the page.',
  accounts_span_companies: 'Every line of an entry must use the same company’s accounts.',
  unbalanced: 'Debits and credits must be equal.',
  already_reversed: 'That entry has already been reversed.',
  cannot_reverse_a_reversal: 'A reversal cannot itself be reversed. Post the original again instead.',
  idempotency_key_reused: 'This form was already used for a different entry. Reload and try again.',
  period_closed: 'The books are closed for that date. Date the entry after the close.',
  not_permitted: 'Only an administrator can close the books.',
  concurrent_change: 'Someone else changed this at the same moment. Try again.',
  app_not_licensed: 'Your organisation does not have the Ledger app enabled.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

export function explain(problemCode: string): string {
  return LEDGER_PROBLEMS[problemCode] ?? 'Something went wrong. Please try again.'
}
