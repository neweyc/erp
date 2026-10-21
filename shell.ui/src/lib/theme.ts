/**
 * Light, dark, or whatever the device prefers. The viewer's own convenience, so it lives in this
 * browser only; public/theme-init.js reads the same key before first paint, and the two must agree.
 */
export type ThemeChoice = 'light' | 'dark' | 'system'

const KEY = 'theme'
const systemDark = () => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false

export function themeChoice(): ThemeChoice {
  try {
    const stored = localStorage.getItem(KEY)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    // Storage blocked (a private window): the device's preference.
    return 'system'
  }
}

export function applyTheme(choice: ThemeChoice): void {
  try {
    if (choice === 'system') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, choice)
  } catch {
    // Not remembered, but still applied for this visit.
  }
  const dark = choice === 'dark' || (choice === 'system' && systemDark())
  document.documentElement.classList.toggle('dark', dark)
}
