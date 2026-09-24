# shell.ui — the single customer-facing SPA

Every app the customer uses is mounted here. Root rules in `../CLAUDE.md` apply;
this file covers the shell specifically.

## The app registry is the only place apps are wired

One declarative entry per app — id, display name, icon, route prefix, lazy component,
required entitlement, required roles, terminology keys — feeds nav, router, permissions,
and the command palette. **Adding an app must never mean edits scattered through the
shell.** If you find yourself touching the nav *and* the router *and* a permissions map,
the registry is missing a field.

## Gating

1. **Entitlement** — not licensed, not in nav, route not registered, and the bundle is
   never fetched (route-level dynamic import). Unlicensed app code must not reach the
   browser.
2. **Role** — licensed but not permitted: hidden, not disabled. Same spirit as EMS's nav.
3. **Neither is access control.** The API enforces both independently. The shell is a
   convenience; treat every gate here as cosmetic.

## Providers, mounted above the router

Session -> entitlements -> terminology -> help. Bundled terminology defaults serve until
the fetch lands so labels never blank out. Routes outside the app shell — landing, login,
invite acceptance, password reset — still get the help provider.

## Layout rules that are load-bearing

- **Wide content scrolls inside its own container, never widens the document.** A
  flex/grid child's automatic minimum size is its *content* width, so a wide table
  silently pushes the page sideways and the browser shrink-to-fits the whole layout on a
  phone. `min-w-0` on the content wrapper and on every DataTable root, `flex-wrap` on
  toolbar action groups. Verify `document.documentElement.scrollWidth === innerWidth` at
  390px — the failure is invisible at desktop widths.
- **Dialogs**: `dismissible={false}` on anything holding a part-filled form (a backdrop
  click otherwise discards entry with no undo). Height capped in `svh`, never `vh` —
  `vh` measures the viewport behind mobile browser chrome and puts the submit button
  out of reach on a phone. A test scans every form dialog for both.
- Idle-timeout warning is measured against the **wall clock**, so a laptop that slept
  counts as idle for the time it slept.

## Cross-app UI

An app referencing another app's data renders it from the **snapshot it stored**, with a
live link only if the reader can reach the source. A ticket assigned to a deleted
employee still shows the name it recorded. Never render a cross-app reference by fetching
from the other app's API inside a list row.
