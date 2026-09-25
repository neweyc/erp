import { api } from '@app-platform/web-api'

export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense'

export interface Account {
  readonly accountId: string
  readonly code: string
  readonly name: string
  readonly type: AccountType
}

export interface EntryLine {
  readonly lineNumber: number
  readonly accountId: string
  readonly accountCode: string
  /** Signed, in minor units: a debit is positive, a credit negative. */
  readonly amountMinor: number
  readonly memo: string | null
}

export interface Entry {
  readonly entryId: string
  readonly fiscalYear: number
  readonly number: number
  readonly entryDate: string
  readonly memo: string
  readonly currency: string
  readonly reversesEntryId: string | null
  readonly reversedByEntryId: string | null
  readonly lines: readonly EntryLine[]
}

export interface AccountBalance {
  readonly accountId: string
  readonly code: string
  readonly name: string
  readonly type: AccountType
  readonly debitMinor: number
  readonly creditMinor: number
}

export interface TrialBalance {
  readonly asOf: string
  readonly currencies: readonly {
    readonly currency: string
    readonly accounts: readonly AccountBalance[]
    readonly totalDebitMinor: number
    readonly totalCreditMinor: number
  }[]
}

export interface Period {
  readonly companyId: string
  readonly companyName: string
  readonly closedThrough: string | null
}

export interface NewLine {
  readonly accountId: string
  readonly amountMinor: number
}

export const listAccounts = () => api<Account[]>('/api/ledger/v1/accounts')

export const createAccount = (code: string, name: string, type: AccountType) =>
  api<{ accountId: string }>('/api/ledger/v1/accounts', {
    method: 'POST',
    body: JSON.stringify({ code, name, type }),
  })

export const listEntries = () => api<Entry[]>('/api/ledger/v1/entries')

/**
 * The idempotency key belongs to the FORM, not to the click: a retry of the same submission after a
 * timeout must send the same key, so the server answers with the entry it already posted instead of
 * posting the money twice.
 */
export const postEntry = (
  entryDate: string, memo: string, currency: string, lines: readonly NewLine[], idempotencyKey: string,
) =>
  api<{ entryId: string; number: number; fiscalYear: number }>('/api/ledger/v1/entries', {
    method: 'POST',
    body: JSON.stringify({ entryDate, memo, currency, lines, idempotencyKey }),
  })

export const reverseEntry = (entryId: string) =>
  api<{ entryId: string }>(`/api/ledger/v1/entries/${entryId}/reversal`, {
    method: 'POST',
    body: JSON.stringify({}),
  })

export const trialBalance = (asOf: string) =>
  api<TrialBalance>(`/api/ledger/v1/trial-balance?asOf=${encodeURIComponent(asOf)}`)

export const listPeriods = () => api<Period[]>('/api/ledger/v1/periods')

export const closePeriod = (closedThrough: string) =>
  api<unknown>('/api/ledger/v1/periods/close', {
    method: 'POST',
    body: JSON.stringify({ closedThrough }),
  })
