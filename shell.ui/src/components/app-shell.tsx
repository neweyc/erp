import { useState, type ReactNode } from 'react'
import { Menu as MenuIcon } from 'lucide-react'
import { Alert, Button, Sheet } from '@app-platform/ui-kit'
import { AppNav } from './app-nav'
import { Brand } from './brand'
import { useSession } from './session-provider'
import { UserMenu } from './user-menu'

/**
 * The frame around every signed-in page: a sidebar on a wide screen; on a phone, a top bar whose
 * menu button slides the same navigation in. The page itself is the one <main>.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { session } = useSession()
  const [menuOpen, setMenuOpen] = useState(false)

  return (
    <div className="min-h-svh">
      {/* Hidden (display: none) below md, which also removes it from the accessibility tree, so a
          page never has two navigations at once. */}
      <aside className="fixed inset-y-0 left-0 z-30 hidden w-64 flex-col border-r border-sidebar-border bg-sidebar md:flex">
        <SidebarContents />
      </aside>

      <Sheet open={menuOpen} onOpenChange={setMenuOpen} title="Menu">
        <SidebarContents onNavigate={() => setMenuOpen(false)} />
      </Sheet>

      {/*
        min-w-0 is load-bearing and easy to delete as noise. A flex child's automatic minimum
        size is its CONTENT width, so one wide table pushes the whole document sideways and the
        browser shrink-to-fits the entire layout on a phone. The failure is invisible at desktop
        widths; check documentElement.scrollWidth === innerWidth at 390px when adding a page.
      */}
      <div className="flex min-h-svh min-w-0 flex-col md:pl-64">
        <header className="sticky top-0 z-20 flex h-14 items-center gap-2 border-b bg-background/85 px-3 backdrop-blur md:hidden">
          <Button variant="ghost" size="icon" aria-label="Open menu" onClick={() => setMenuOpen(true)}>
            <MenuIcon />
          </Button>
          <Brand className="text-sm" />
          <div className="ml-auto">
            <UserMenu compact />
          </div>
        </header>

        <main className="min-w-0 flex-1 px-4 py-6 sm:px-6 lg:px-10 lg:py-10">
          <div className="mx-auto grid w-full max-w-6xl min-w-0 gap-6">
            {session?.tenantSuspended && (
              // Stated, not hidden. A suspended tenant keeps auth and export and is refused
              // everything else with a 403; without this, those refusals look like bugs.
              <Alert tone="warning" title="Account suspended">
                This organization’s account is suspended. You can still sign in and export your data.
              </Alert>
            )}
            {children}
          </div>
        </main>
      </div>
    </div>
  )
}

function SidebarContents({ onNavigate }: { onNavigate?: () => void }) {
  const { session } = useSession()
  return (
    <>
      <div className="flex h-16 shrink-0 items-center px-5">
        <Brand />
      </div>
      {session && (
        <div className="mx-3 mb-4 rounded-lg border bg-background/60 px-3 py-2.5">
          <p className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">Organization</p>
          <p className="truncate text-sm font-medium">{session.tenantName}</p>
        </div>
      )}
      <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-4">
        <AppNav onNavigate={onNavigate} />
      </div>
      <div className="shrink-0 border-t border-sidebar-border p-3">
        <UserMenu />
      </div>
    </>
  )
}
