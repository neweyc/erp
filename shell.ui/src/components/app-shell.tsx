import type { ReactNode } from 'react'
import { AppNav } from './app-nav'
import { useSession } from './session-provider'

export function AppShell({ children }: { children: ReactNode }) {
  const { session } = useSession()

  return (
    <div className="shell">
      <AppNav />
      {/*
        min-w-0 is load-bearing and easy to delete as noise. A flex child's automatic minimum
        size is its CONTENT width, so one wide table pushes the whole document sideways and the
        browser shrink-to-fits the entire layout on a phone. The failure is invisible at desktop
        widths; check documentElement.scrollWidth === innerWidth at 390px when adding a page.
      */}
      <div className="shell-content" style={{ minWidth: 0 }}>
        {session?.tenantSuspended && (
          // Stated, not hidden. A suspended tenant keeps auth and export and is refused
          // everything else with a 403; without this, those refusals look like bugs.
          <p role="alert">
            This organisation’s account is suspended. You can still sign in and export your data.
          </p>
        )}
        {children}
      </div>
    </div>
  )
}
