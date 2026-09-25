import type { TrialBalance } from './api'
import { fromMinor } from './money'
import { Scrolls } from './Scrolls'

export function TrialBalanceSection({
  balance, asOf, onAsOfChange,
}: {
  balance: TrialBalance | null
  asOf: string
  onAsOfChange: (date: string) => void
}) {
  return (
    <section aria-labelledby="trial-balance-heading">
      <h2 id="trial-balance-heading">Trial balance</h2>

      <label htmlFor="as-of">As of</label>
      <input id="as-of" type="date" value={asOf} onChange={(e) => e.target.value && onAsOfChange(e.target.value)} />

      {!balance || balance.currencies.length === 0 ? (
        <p>No balances as of this date.</p>
      ) : (
        balance.currencies.map((c) => (
          // One table per currency: amounts in different currencies are never added together.
          <Scrolls key={c.currency}>
            <table aria-label={`Trial balance in ${c.currency}`}>
              <thead>
                <tr><th>Account</th><th>Debit</th><th>Credit</th></tr>
              </thead>
              <tbody>
                {c.accounts.map((a) => (
                  <tr key={a.accountId}>
                    <td>{a.code} — {a.name}</td>
                    <td>{a.debitMinor ? fromMinor(a.debitMinor) : ''}</td>
                    <td>{a.creditMinor ? fromMinor(a.creditMinor) : ''}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <th scope="row">Total {c.currency}</th>
                  <td>{fromMinor(c.totalDebitMinor)}</td>
                  <td>{fromMinor(c.totalCreditMinor)}</td>
                </tr>
              </tfoot>
            </table>
          </Scrolls>
        ))
      )}
    </section>
  )
}
