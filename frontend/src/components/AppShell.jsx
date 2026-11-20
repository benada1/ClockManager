import { useState } from 'react'
import { useAuth } from '../auth/authContext.js'
import { href, useHashRoute } from '../router.js'
import Avatar from './Avatar.jsx'
import Button from './Button.jsx'
import { ClockIcon } from './icons.jsx'
import ThemeToggle from './ThemeToggle.jsx'

const NAV = [
  { path: '/', label: 'Clock' },
  { path: '/history', label: 'History' },
]
const MANAGER_NAV = { path: '/manager', label: 'Manager' }

function Brand() {
  return (
    <a href={href('/')} className="inline-flex min-h-11 items-center gap-2.5 text-base font-semibold text-ink no-underline">
      <span className="inline-flex size-8 items-center justify-center rounded-control bg-accent text-on-accent">
        <ClockIcon />
      </span>
      Clock Manager
    </a>
  )
}

function TimeLabLink() {
  return (
    <a className="text-muted underline-offset-4 hover:underline" href={href('/time-lab')}>
      Time lab (developer tool)
    </a>
  )
}

/**
 * Frame of every screen. Signed in: brand, the segmented nav, theme, who is logged in, Log out.
 * Signed out (log in, sign up, loading): brand and theme only.
 */
export default function AppShell({ children }) {
  const { user, logout } = useAuth()
  const path = useHashRoute()
  const [busy, setBusy] = useState(false)
  // Only a hint: the server checks the role on every manager request.
  const nav = user?.role === 'Manager' ? [...NAV, MANAGER_NAV] : NAV
  const isCurrent = (p) => p === path || (p === '/manager' && path.startsWith('/manager/'))

  return (
    <div className="ui flex min-h-screen flex-col bg-canvas font-sans text-ink antialiased">
      <header className={user ? 'border-b border-line bg-surface' : ''}>
        <div className={`mx-auto flex w-full max-w-6xl flex-wrap items-center gap-x-4 gap-y-2 px-4 sm:px-6 ${user ? 'py-3' : 'py-5'}`}>
          <div className="flex flex-1 basis-0">
            <Brand />
          </div>
          {user && (
            <nav aria-label="Main" className="order-last flex w-full gap-1 rounded-field bg-subtle p-1 md:order-none md:w-auto">
              {nav.map((item) => {
                const current = isCurrent(item.path)
                return (
                  <a
                    key={item.path}
                    href={href(item.path)}
                    aria-current={current ? 'page' : undefined}
                    className={`inline-flex min-h-10 flex-1 items-center justify-center rounded-control px-4 text-sm font-medium no-underline transition-colors md:flex-none ${
                      current ? 'bg-surface text-ink shadow-card' : 'text-ink-3 hover:bg-hover hover:text-ink'
                    }`}
                  >
                    {item.label}
                  </a>
                )
              })}
            </nav>
          )}
          <div className="flex flex-1 basis-0 items-center justify-end gap-2.5">
            <ThemeToggle />
            {user && (
              <>
                <span className="hidden min-w-0 items-center gap-2 text-sm text-ink-2 sm:flex">
                  <Avatar name={user.displayName} />
                  <span className="max-w-40 truncate">{user.displayName}</span>
                </span>
                <Button
                  variant="secondary"
                  busy={busy}
                  busyLabel="Logging out…"
                  onClick={async () => {
                    setBusy(true)
                    await logout()
                    setBusy(false)
                  }}
                >
                  Log out
                </Button>
              </>
            )}
          </div>
        </div>
      </header>

      <main className="mx-auto flex w-full max-w-6xl flex-1 flex-col px-4 py-8 sm:px-6 md:py-10">{children}</main>

      <footer className="mx-auto flex w-full max-w-6xl flex-wrap justify-between gap-4 px-4 py-5 text-sm text-muted sm:px-6">
        {user ? <span /> : <span>All times are Zurich time.</span>}
        <TimeLabLink />
      </footer>
    </div>
  )
}
