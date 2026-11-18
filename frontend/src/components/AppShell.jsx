import { useState } from 'react'
import { useAuth } from '../auth/authContext.js'
import { href, useHashRoute } from '../router.js'
import Button from './Button.jsx'

const NAV = [
  { path: '/', label: 'Clock' },
  { path: '/history', label: 'History' },
]
const MANAGER_NAV = { path: '/manager', label: 'Manager' }

/** Frame of the new screens: brand, the two pages, who is logged in, Log out. */
export default function AppShell({ children }) {
  const { user, logout } = useAuth()
  const path = useHashRoute()
  const [busy, setBusy] = useState(false)
  // Only a hint: the server checks the role on every manager request.
  const nav = user?.role === 'Manager' ? [...NAV, MANAGER_NAV] : NAV
  const isCurrent = (p) => p === path || (p === '/manager' && path.startsWith('/manager/'))

  return (
    <div className="ui flex min-h-screen flex-col bg-canvas font-sans text-ink">
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex min-h-16 w-full max-w-5xl flex-wrap items-center justify-between gap-x-4 gap-y-1 px-4 py-2">
          <span className="text-base font-semibold">Clock Manager</span>
          {user && (
            <nav aria-label="Main" className="order-last flex w-full gap-1 sm:order-none sm:w-auto">
              {nav.map((item) => (
                <a
                  key={item.path}
                  href={href(item.path)}
                  aria-current={isCurrent(item.path) ? 'page' : undefined}
                  className={`inline-flex min-h-11 items-center rounded-ui px-4 text-base no-underline ${
                    isCurrent(item.path) ? 'bg-accent-soft font-semibold text-accent-strong' : 'text-ink hover:bg-canvas'
                  }`}
                >
                  {item.label}
                </a>
              ))}
            </nav>
          )}
          {user && (
            <div className="flex items-center gap-3">
              <span className="text-sm text-muted">{user.displayName}</span>
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
            </div>
          )}
        </div>
      </header>
      <main className="mx-auto w-full max-w-5xl flex-1 px-4 py-6 md:py-8">{children}</main>
      <footer className="mx-auto w-full max-w-5xl px-4 pb-6 text-sm text-muted">
        <a className="underline" href={href('/time-lab')}>
          Time lab (developer tool)
        </a>
      </footer>
    </div>
  )
}
