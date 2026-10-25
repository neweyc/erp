// Applies the theme before the first paint, so a dark-mode reader never sees a white flash. The
// same file as the shell's (shell.ui/public/theme-init.js); the console has no theme switcher, so
// it follows the device unless a choice was stored on this origin. A file rather than an inline
// script, so a strict Content-Security-Policy (no 'unsafe-inline') can allow it.
try {
  var choice = localStorage.getItem('theme')
  var dark = choice === 'dark' || (choice !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
} catch (e) {
  // Storage blocked (a private window, a locked-down browser): the light theme, which is fine.
}
