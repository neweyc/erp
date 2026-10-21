import { Link } from 'react-router-dom'
import { ArrowRight, LayoutGrid, ShieldCheck, Sparkles, Users } from 'lucide-react'
import { Permissions } from '@app-platform/web-api'
import { Alert, Card, EmptyState, PageHeader, PageStack } from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { APPS, visibleApps } from '../lib/apps'

/**
 * Where a signed-in person lands: the organization, the apps they may open, and, for an
 * administrator, the way to people and roles.
 */
export function Dashboard() {
  const { session } = useSession()

  if (!session) return null

  const available = visibleApps(session.licensedApps, session.permissions)
  const unlicensed = APPS.filter((app) => !session.licensedApps.includes(app.id)).length

  // Licensing an app grants nobody a role in it, the administrator included. Without this an
  // administrator who has just licensed an app sees nothing change and concludes it is broken.
  const licensedWithoutRole = APPS.filter(
    (app) => session.licensedApps.includes(app.id) && !available.includes(app),
  )
  const canManageUsers = session.permissions.includes(Permissions.core.userManage)
  const canSeeUsers = session.permissions.includes(Permissions.core.userRead)

  return (
    <PageStack>
      <PageHeader
        title={session.tenantName}
        description={
          <>
            Signed in as <span className="font-medium text-foreground">{session.email}</span>
            {session.roles.length > 0 && <> · {session.roles.map((r) => r.name).join(', ')}</>}
          </>
        }
      />

      {canManageUsers && licensedWithoutRole.length > 0 && (
        <Alert tone="info" title="An app is waiting for a role">
          You have no role yet in {licensedWithoutRole.map((app) => app.name).join(', ')}. Give
          yourself or others one on the{' '}
          <Link to="/users" className="font-medium text-primary underline-offset-4 hover:underline">
            Users
          </Link>{' '}
          page.
        </Alert>
      )}

      <section aria-labeledby="apps-heading" className="grid gap-3">
        <h2 id="apps-heading" className="text-sm font-medium text-muted-foreground">
          Your applications
        </h2>
        {available.length === 0 ? (
          <Card>
            {/* An empty state that says what to do. "No applications" alone reads as a fault. */}
            <EmptyState icon={LayoutGrid} title="No applications yet">
              No applications are available to you yet. Your administrator can give you access.
            </EmptyState>
          </Card>
        ) : (
          <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {available.map((app) => (
              <li key={app.id}>
                <Tile to={app.path} icon={app.icon} title={app.name} description={app.description} />
              </li>
            ))}
          </ul>
        )}
      </section>

      {canSeeUsers && (
        <section aria-labeledby="admin-heading" className="grid gap-3">
          <h2 id="admin-heading" className="text-sm font-medium text-muted-foreground">
            Administration
          </h2>
          <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            <li>
              <Tile to="/users" icon={Users} title="People and access" description="Invite, deactivate, and give roles." />
            </li>
            <li>
              <Tile to="/roles" icon={ShieldCheck} title="Roles" description="What each role allows; make your own." />
            </li>
          </ul>
        </section>
      )}

      {unlicensed > 0 && (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Sparkles className="size-4" />
          {unlicensed === 1
            ? '1 further application is available to license.'
            : `${unlicensed} further applications are available to license.`}
        </p>
      )}
    </PageStack>
  )
}

/** A card that is one link: the whole card is the click target. */
function Tile({
  to, icon: Icon, title, description,
}: {
  to: string
  icon: React.ComponentType<{ className?: string }>
  title: string
  description: string
}) {
  return (
    <Link
      to={to}
      className="group flex h-full flex-col gap-4 rounded-xl border bg-card p-5 shadow-xs transition-[border-color,box-shadow] outline-none hover:border-primary/40 hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring/50"
    >
      <span className="flex size-10 items-center justify-center rounded-lg bg-primary/10 text-primary">
        <Icon className="size-5" />
      </span>
      <span className="grid gap-1">
        <span className="flex items-center gap-1.5 font-medium">
          {title}
          <ArrowRight className="size-4 -translate-x-1 text-muted-foreground opacity-0 transition-all group-hover:translate-x-0 group-hover:opacity-100" />
        </span>
        <span className="text-sm text-muted-foreground">{description}</span>
      </span>
    </Link>
  )
}
