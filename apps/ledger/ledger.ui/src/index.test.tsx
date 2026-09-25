import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import LedgerApp from './index'
import { fromMinor, toMinor } from './money'
import { lineAmount } from './PostEntryForm'

const cash = { accountId: 'acct_cash', code: '1000', name: 'Cash', type: 'Asset' as const }
const sales = { accountId: 'acct_sales', code: '4000', name: 'Sales', type: 'Revenue' as const }

const entry = (over: Record<string, unknown> = {}) => ({
  entryId: 'je_one', fiscalYear: 2026, number: 1, entryDate: '2026-09-01', memo: 'First sale', currency: 'USD',
  reversesEntryId: null, reversedByEntryId: null,
  lines: [
    { lineNumber: 1, accountId: 'acct_cash', accountCode: '1000', amountMinor: 1250, memo: null },
    { lineNumber: 2, accountId: 'acct_sales', accountCode: '4000', amountMinor: -1250, memo: null },
  ],
  ...over,
})

/** Routes each request by URL and method; records every POST body so a test can read what was sent. */
function stubApi(routes: Record<string, () => { status?: number; body: unknown }>) {
  const posted: { url: string; body: Record<string, unknown> }[] = []

  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    if (method === 'POST') posted.push({ url, body: JSON.parse(String(init?.body ?? '{}')) })

    const key = Object.keys(routes).find((k) => `${method} ${url}`.includes(k))
    const { status = 200, body } = key ? routes[key]!() : { body: [] }
    return Promise.resolve({ ok: status < 400, status, json: async () => body } as Response)
  }))

  return posted
}

const standardRoutes = (entries: unknown[] = [], extra: Record<string, () => { status?: number; body: unknown }> = {}) => ({
  ...extra,
  'GET /api/ledger/v1/accounts': () => ({ body: [cash, sales] }),
  'GET /api/ledger/v1/entries': () => ({ body: entries }),
  'GET /api/ledger/v1/periods': () => ({ body: [{ companyId: 'co_a', companyName: 'Acme Ltd', closedThrough: null }] }),
  'GET /api/ledger/v1/trial-balance': () => ({ body: { asOf: '2026-09-25', currencies: [] } }),
})

afterEach(() => vi.unstubAllGlobals())

describe('money', () => {
  it.each([
    ['12.50', 1250], ['7', 700], ['0.5', 50], ['12.10', 1210], [' 3.07 ', 307],
  ])('reads %s as %i minor units, exactly', (text, minor) => {
    // 12.10 * 100 is 1209.999… in floating point; the string arithmetic must give 1210.
    expect(toMinor(text)).toBe(minor)
  })

  it.each(['', '0', '-3', '1e3', '1.234', 'abc', '1,000'])('refuses %j rather than guess', (text) => {
    expect(toMinor(text)).toBeNull()
  })

  it.each([[1250, '12.50'], [-1250, '-12.50'], [5, '0.05'], [1234567, '12,345.67'], [0, '0.00']])(
    'shows %i minor units as %s', (minor, text) => expect(fromMinor(minor)).toBe(text))
})

describe('a posting line', () => {
  it('is a debit, a credit, or incomplete — never both', () => {
    expect(lineAmount({ accountId: 'a', debit: '10', credit: '' })).toBe(1000)
    expect(lineAmount({ accountId: 'a', debit: '', credit: '10' })).toBe(-1000)
    expect(lineAmount({ accountId: 'a', debit: '10', credit: '10' })).toBeNull()
    expect(lineAmount({ accountId: '', debit: '10', credit: '' })).toBeNull()
    expect(lineAmount({ accountId: 'a', debit: '10.001', credit: '' })).toBeNull()
  })
})

describe('ledger app', () => {
  async function fillBalancedEntry(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('Memo'), 'Sale')
    await user.selectOptions(screen.getByLabelText('Line 1 account'), 'acct_cash')
    await user.type(screen.getByLabelText('Line 1 debit'), '12.50')
    await user.selectOptions(screen.getByLabelText('Line 2 account'), 'acct_sales')
    await user.type(screen.getByLabelText('Line 2 credit'), '12.5')
  }

  it('will not post until the entry balances, and says by how much it is out', async () => {
    stubApi(standardRoutes())
    const user = userEvent.setup()
    render(<LedgerApp />)
    await screen.findByRole('heading', { name: 'Ledger' })

    await user.selectOptions(screen.getByLabelText('Line 1 account'), 'acct_cash')
    await user.type(screen.getByLabelText('Line 1 debit'), '10')
    await user.selectOptions(screen.getByLabelText('Line 2 account'), 'acct_sales')
    await user.type(screen.getByLabelText('Line 2 credit'), '9.99')
    await user.type(screen.getByLabelText('Memo'), 'Sale')

    expect(screen.getByText('Out of balance by 0.01.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Post entry' })).toBeDisabled()
  })

  it('posts signed minor units, and a retry after a failure sends the same idempotency key', async () => {
    let fail = true
    const posted = stubApi(standardRoutes([], {
      'POST /api/ledger/v1/entries': () => (fail ? { status: 504, body: { problemCode: 'unknown' } } : { body: { entryId: 'je_new' } }),
    }))
    const user = userEvent.setup()
    render(<LedgerApp />)
    await screen.findByRole('heading', { name: 'Ledger' })

    await fillBalancedEntry(user)
    expect(screen.getByText('Balanced.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Post entry' }))
    await screen.findByRole('alert')

    fail = false
    await user.click(screen.getByRole('button', { name: 'Post entry' }))
    await waitFor(() => expect(posted).toHaveLength(2))

    const [first, retry] = posted
    expect(first!.body.lines).toEqual([
      { accountId: 'acct_cash', amountMinor: 1250 },
      { accountId: 'acct_sales', amountMinor: -1250 },
    ])
    // The same submission retried: the server must be able to recognise it and not post twice.
    expect(retry!.body.idempotencyKey).toBe(first!.body.idempotencyKey)
  })

  it('offers Reverse only where the server would allow it', async () => {
    stubApi(standardRoutes([
      entry({ entryId: 'je_live' }),
      entry({ entryId: 'je_orig', number: 2, memo: 'Mistake', reversedByEntryId: 'je_rev' }),
      entry({ entryId: 'je_rev', number: 3, memo: 'Reversal', reversesEntryId: 'je_orig' }),
    ]))
    render(<LedgerApp />)

    const live = await screen.findByRole('article', { name: 'Entry 2026-1' })
    const original = screen.getByRole('article', { name: 'Entry 2026-2' })
    const reversal = screen.getByRole('article', { name: 'Entry 2026-3' })

    expect(within(live).getByRole('button', { name: 'Reverse' })).toBeInTheDocument()
    expect(within(original).queryByRole('button', { name: 'Reverse' })).toBeNull()
    expect(within(original).getByText('Reversed by 2026-3.')).toBeInTheDocument()
    expect(within(reversal).queryByRole('button', { name: 'Reverse' })).toBeNull()
    expect(within(reversal).getByText('Reverses 2026-2.')).toBeInTheDocument()
  })

  it('explains a closed period in words, keeping what was typed', async () => {
    stubApi(standardRoutes([], {
      'POST /api/ledger/v1/entries': () => ({ status: 409, body: { problemCode: 'period_closed' } }),
    }))
    const user = userEvent.setup()
    render(<LedgerApp />)
    await screen.findByRole('heading', { name: 'Ledger' })

    await fillBalancedEntry(user)
    await user.click(screen.getByRole('button', { name: 'Post entry' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The books are closed for that date')
    expect(screen.getByLabelText('Memo')).toHaveValue('Sale')
  })

  it('shows the trial balance per currency with its totals', async () => {
    stubApi({
      ...standardRoutes(),
      'GET /api/ledger/v1/trial-balance': () => ({
        body: {
          asOf: '2026-09-25',
          currencies: [{
            currency: 'USD',
            accounts: [
              { ...cash, debitMinor: 1250, creditMinor: 0 },
              { ...sales, debitMinor: 0, creditMinor: 1250 },
            ],
            totalDebitMinor: 1250,
            totalCreditMinor: 1250,
          }],
        },
      }),
    })
    render(<LedgerApp />)

    const table = await screen.findByRole('table', { name: 'Trial balance in USD' })
    expect(within(table).getByRole('row', { name: /Total USD/ })).toHaveTextContent('12.50')
  })
})
