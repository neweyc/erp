import type { ComponentType } from 'react'
import { NavLink } from 'react-router-dom'
import { BriefcaseBusiness, LayoutDashboard, MapPin, Network, ShieldCheck, Users, UsersRound } from 'lucide-react'
import { Permissions } from '@app-platform/web-api'
import { cn } from '@app-platform/ui-kit'
import { visibleApps } from '../lib/apps'
import { useSession } from './session-provider'

/**
 * The navigation: the dashboard, the apps this viewer may open (from the registry), and
 * administration for those allowed it. Rendered in the sidebar, and in the slide-in menu on a phone,
 * which passes `onNavigate` to close itself.
 */
export function AppNav({ onNavigate }: { onNavigate?: () => void }) {
  const { session } = useSession()

  if (!session) return null

  const apps = visibleApps(session.licensedApps, session.permissions)
  const canSeeUsers = session.permissions.includes(Permissions.core.userRead)
  const canSeeRoster = session.permissions.includes(Permissions.core.employeeRead)

  return (
    <nav aria-label="Applications" className="grid gap-6">
      <NavSection title="Workspace">
        <NavItem to="/dashboard" icon={LayoutDashboard} label="Dashboard" onNavigate={onNavigate} />
        {/* Unavailable apps are ABSENT, not disabled. A disabled item advertises something the
            reader cannot have and invites a support conversation about it. */}
        {apps.map((app) => (
          <NavItem key={app.id} to={app.path} icon={app.icon} label={app.name} onNavigate={onNavigate} />
        ))}
      </NavSection>

      {canSeeRoster && (
        <NavSection title="Organization">
          <NavItem to="/employees" icon={UsersRound} label="Employees" onNavigate={onNavigate} />
          <NavItem to="/departments" icon={Network} label="Departments" onNavigate={onNavigate} />
          <NavItem to="/job-titles" icon={BriefcaseBusiness} label="Job titles" onNavigate={onNavigate} />
          <NavItem to="/locations" icon={MapPin} label="Locations" onNavigate={onNavigate} />
        </NavSection>
      )}

      {canSeeUsers && (
        <NavSection title="Administration">
          <NavItem to="/users" icon={Users} label="Users" onNavigate={onNavigate} />
          <NavItem to="/roles" icon={ShieldCheck} label="Roles" onNavigate={onNavigate} />
        </NavSection>
      )}
    </nav>
  )
}

function NavSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="grid gap-1">
      <p className="px-3 pb-1 text-[11px] font-semibold tracking-wider text-muted-foreground/80 uppercase">{title}</p>
      <ul className="grid gap-0.5">{children}</ul>
    </div>
  )
}

function NavItem({
  to, icon: Icon, label, onNavigate,
}: {
  to: string
  icon: ComponentType<{ className?: string }>
  label: string
  onNavigate?: () => void
}) {
  return (
    <li>
      <NavLink
        to={to}
        onClick={onNavigate}
        className={({ isActive }) =>
          cn(
            'group flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
            isActive
              ? 'bg-accent text-accent-foreground'
              : 'text-sidebar-foreground hover:bg-accent/60 hover:text-foreground',
          )
        }
      >
        {({ isActive }) => (
          <>
            {/* Lucide icons are aria-hidden already: the label names the link. */}
            <Icon
              className={cn('size-4 shrink-0', isActive ? 'text-primary' : 'text-muted-foreground group-hover:text-foreground')}
            />
            {label}
          </>
        )}
      </NavLink>
    </li>
  )
}
