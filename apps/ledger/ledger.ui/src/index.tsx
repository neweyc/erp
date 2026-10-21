import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, Permissions, type AppProps } from '@app-platform/web-api'
import { Alert, Loading, PageHeader, PageStack } from '@app-platform/ui-kit'
import {
  closePeriod, createAccount, listAccounts, listEntries, listPeriods, postEntry, reverseEntry, trialBalance,
  type Account, type Entry, type Period, type TrialBalance,
} from './api'
import { explain } from './messages'
import { AccountsSection } from './AccountsSection'
import { PostEntryForm } from './PostEntryForm'
import { JournalSection } from './JournalSection'
import { TrialBalanceSection } from './TrialBalanceSection'
import { PeriodSection } from './PeriodSection'
import { today } from './dates'

/**
 * The ledger — a proof of concept, one page: the chart of accounts, posting, the journal with its
 * reversals, the trial balance, and closing the books. Everything that changes the books is visible
 * on the same screen as its effect, which is what makes the invariants demonstrable.
 *
 * What the user's ledger role does not allow is hidden: a viewer reads, an accountant also posts
 * and reverses, a controller also adds accounts and closes periods. The API enforces each.
 */
export default function LedgerApp({ permissions }: AppProps) {
  const can = (permission: string) => permissions.includes(permission)

  const [accounts, setAccounts] = useState<Account[]>([])
  const [entries, setEntries] = useState<Entry[]>([])
  const [periods, setPeriods] = useState<Period[]>([])
  const [balance, setBalance] = useState<TrialBalance | null>(null)
  const [asOf, setAsOf] = useState(today)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  /** A response from an earlier load is discarded, so a slow reload cannot overwrite a newer one. */
  const latestRequest = useRef(0)

  const refresh = useCallback(async (balanceAsOf: string) => {
    const request = ++latestRequest.current

    try {
      const [accountRows, entryRows, periodRows, balanceRow] = await Promise.all([
        listAccounts(), listEntries(), listPeriods(), trialBalance(balanceAsOf),
      ])
      if (request !== latestRequest.current) return

      setAccounts(accountRows)
      setEntries(entryRows)
      setPeriods(periodRows)
      setBalance(balanceRow)
      setError(null)
    } catch (caught) {
      if (request !== latestRequest.current) return
      setError(caught instanceof ApiError ? explain(caught.problemCode) : 'Could not load the ledger.')
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh(asOf)
  }, [refresh, asOf])

  /**
   * Runs a change, then reloads everything from the server rather than patching local state — a
   * post moves the journal AND the trial balance, and the page should show what the books now say.
   * The message is set after the reload, which would otherwise clear it.
   */
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setError(null)
    let failure: string | null = null

    try {
      await action()
    } catch (caught) {
      failure = caught instanceof ApiError ? explain(caught.problemCode) : 'Something went wrong.'
    }

    await refresh(asOf)

    if (failure === null) return true
    setError(failure)
    return false
  }

  if (loading) return <Loading>Loading the ledger…</Loading>

  const canPost = can(Permissions.ledger.entryPost)

  return (
    <PageStack>
      <PageHeader
        title="Ledger"
        description="Double-entry books: every entry balances, posted entries are corrected by reversal, and a closed period stays closed."
      />

      {error && <Alert tone="danger">{error}</Alert>}

      {/* Two columns on a wide screen: the work (posting, the journal) on the left, the state of the
          books (balances, accounts, periods) on the right. One column on a phone, work first. */}
      <div className="grid min-w-0 gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,26rem)]">
        <div className="grid min-w-0 content-start gap-6">
          {canPost && (
            <PostEntryForm
              accounts={accounts}
              onPost={(date, memo, currency, lines, key) => run(() => postEntry(date, memo, currency, lines, key))}
            />
          )}

          <JournalSection
            entries={entries}
            onReverse={can(Permissions.ledger.entryReverse) ? (entryId) => run(() => reverseEntry(entryId)) : null}
          />
        </div>

        <div className="grid min-w-0 content-start gap-6">
          <TrialBalanceSection balance={balance} asOf={asOf} onAsOfChange={setAsOf} />

          <AccountsSection
            accounts={accounts}
            onCreate={
              can(Permissions.ledger.accountCreate)
                ? (code, name, type) => run(() => createAccount(code, name, type))
                : null
            }
          />

          <PeriodSection
            periods={periods}
            onClose={can(Permissions.ledger.periodClose) ? (through) => run(() => closePeriod(through)) : null}
          />
        </div>
      </div>
    </PageStack>
  )
}
