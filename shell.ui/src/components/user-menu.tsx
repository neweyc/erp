import { useState } from 'react'
import { ChevronsUpDown, LogOut, Monitor, Moon, Sun } from 'lucide-react'
import {
  Avatar, cn, Menu, MenuContent, MenuItem, MenuLabel, MenuRadioGroup, MenuRadioItem, MenuSeparator, MenuTrigger,
} from '@app-platform/ui-kit'
import { api } from '../lib/session'
import { applyTheme, themeChoice, type ThemeChoice } from '../lib/theme'
import { useSession } from './session-provider'

/**
 * Who is signed in, to which organization, with the theme choice and signing out. `compact` is the
 * phone header's version: the avatar alone.
 */
export function UserMenu({ compact = false }: { compact?: boolean }) {
  const { session, signedOut } = useSession()
  const [theme, setTheme] = useState<ThemeChoice>(themeChoice)

  if (!session) return null

  async function signOut() {
    try {
      await api('/api/core/v1/auth/sign-out', { method: 'POST' })
    } catch {
      // Already signed out, or the server is unreachable: either way this browser leaves the
      // signed-in screens, and the cookie is useless to anyone once the session row is revoked.
    }
    signedOut('signed_out')
  }

  return (
    <Menu>
      <MenuTrigger
        aria-label={`Account: ${session.email}`}
        className={cn(
          'flex items-center gap-3 rounded-md text-left outline-none transition-colors hover:bg-accent/60 focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=open]:bg-accent',
          compact ? 'p-1' : 'w-full px-2 py-2',
        )}
      >
        <Avatar name={session.email} />
        {!compact && (
          <>
            <span className="grid min-w-0 flex-1 leading-tight">
              <span className="truncate text-sm font-medium">{session.email}</span>
              <span className="truncate text-xs text-muted-foreground">{session.tenantName}</span>
            </span>
            <ChevronsUpDown className="size-4 shrink-0 text-muted-foreground" />
          </>
        )}
      </MenuTrigger>
      <MenuContent align={compact ? 'end' : 'start'} side={compact ? 'bottom' : 'top'} className="w-64">
        <MenuLabel className="grid gap-0.5">
          <span className="truncate text-sm font-medium text-foreground">{session.email}</span>
          <span className="truncate font-normal">
            {session.roles.length > 0 ? session.roles.map((r) => r.name).join(', ') : 'No roles'}
          </span>
        </MenuLabel>
        <MenuSeparator />
        <MenuLabel>Theme</MenuLabel>
        <MenuRadioGroup
          value={theme}
          onValueChange={(value) => {
            const choice = value as ThemeChoice
            setTheme(choice)
            applyTheme(choice)
          }}
        >
          <MenuRadioItem value="light">
            <Sun /> Light
          </MenuRadioItem>
          <MenuRadioItem value="dark">
            <Moon /> Dark
          </MenuRadioItem>
          <MenuRadioItem value="system">
            <Monitor /> Match this device
          </MenuRadioItem>
        </MenuRadioGroup>
        <MenuSeparator />
        <MenuItem onSelect={() => void signOut()}>
          <LogOut /> Sign out
        </MenuItem>
      </MenuContent>
    </Menu>
  )
}
