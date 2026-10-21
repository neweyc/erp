// Applies the theme before the first paint, so a dark-mode reader never sees a white flash. A file
// rather than an inline script, so a strict Content-Security-Policy (no 'unsafe-inline') can allow
// it. Kept in step with src/lib/theme.ts, which owns the choice once the app has started.
try {
  var choice = localStorage.getItem('theme')
  var dark = choice === 'dark' || (choice !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
} catch (e) {
  // Storage blocked (a private window, a locked-down browser): the light theme, which is fine.
}
