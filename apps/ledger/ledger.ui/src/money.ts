/**
 * Money on the screen: what a person types ("12.50") to and from what the ledger stores (1250, in
 * minor units).
 *
 * String arithmetic, never floating point. `12.10 * 100` is 1209.9999999999998 in JavaScript, and a
 * ledger that is off by a cent because of how the browser multiplies is exactly the defect the
 * server's integer amounts exist to rule out.
 *
 * Two decimal places. That is right for most currencies and wrong for some (JPY has none, KWD three);
 * which currencies are supported, and their places, is an open question (docs/open-questions.md), so
 * this is the one assumption the UI makes and it is stated here rather than scattered.
 */
export const DECIMAL_PLACES = 2

/**
 * "12.50" → 1250, "7" → 700, "0.5" → 50. Null for anything that is not a plain positive amount with at
 * most two decimals — including "", "-3", "1e3" and "1.234" — so the form can refuse it rather than
 * guess what was meant.
 */
export function toMinor(text: string): number | null {
  const match = /^(\d{1,15})(?:\.(\d{1,2}))?$/.exec(text.trim())
  if (!match) return null

  const whole = match[1]!
  const fraction = (match[2] ?? '').padEnd(DECIMAL_PLACES, '0')
  const minor = Number(whole + fraction)

  return minor > 0 ? minor : null
}

/** 1250 → "12.50", -1250 → "-12.50", 1234567 → "12,345.67". */
export function fromMinor(minor: number): string {
  const sign = minor < 0 ? '-' : ''
  const digits = String(Math.abs(minor)).padStart(DECIMAL_PLACES + 1, '0')
  const whole = digits.slice(0, -DECIMAL_PLACES).replace(/\B(?=(\d{3})+(?!\d))/g, ',')

  return `${sign}${whole}.${digits.slice(-DECIMAL_PLACES)}`
}
