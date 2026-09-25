import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '@app-platform/web-api'
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
 */
export default function LedgerApp() {
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

  if (loading) return <p role="status">Loading the ledger…</p>

  return (
    <main>
      <h1>Ledger</h1>

      {error && <p role="alert">{error}</p>}

      <PeriodSection
        periods={periods}
        onClose={(through) => run(() => closePeriod(through))}
      />

      <AccountsSection
        accounts={accounts}
        onCreate={(code, name, type) => run(() => createAccount(code, name, type))}
      />

      <PostEntryForm
        accounts={accounts}
        onPost={(date, memo, currency, lines, key) => run(() => postEntry(date, memo, currency, lines, key))}
      />

      <JournalSection
        entries={entries}
        onReverse={(entryId) => run(() => reverseEntry(entryId))}
      />

      <TrialBalanceSection balance={balance} asOf={asOf} onAsOfChange={setAsOf} />
    </main>
  )
}
