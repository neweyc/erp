import { cn } from '@app-platform/ui-kit'

/** The product's mark and name. One component, so the sidebar, the phone header and sign-in match. */
export function Brand({ className }: { className?: string }) {
  return (
    <span className={cn('flex items-center gap-2.5 font-semibold tracking-tight', className)}>
      <svg aria-hidden viewBox="0 0 32 32" className="size-7 shrink-0">
        <rect width="32" height="32" rx="8" className="fill-primary" />
        <path
          d="M9 21.5 16 9l7 12.5"
          fill="none"
          className="stroke-primary-foreground"
          strokeWidth="3"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
        <circle cx="16" cy="21.5" r="2" className="fill-primary-foreground" />
      </svg>
      <span>App Platform</span>
    </span>
  )
}
