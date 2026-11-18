import { useState } from 'react'
import { useAuth } from '../auth/authContext.js'
import { href } from '../router.js'
import Button from './Button.jsx'

/** Frame of the new screens: brand, who is logged in, Log out. */
export default function AppShell({ children }) {
  const { user, logout } = useAuth()
  const [busy, setBusy] = useState(false)

  return (
    <div className="ui flex min-h-screen flex-col bg-canvas font-sans text-ink">
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex min-h-16 w-full max-w-3xl items-center justify-between gap-4 px-4">
          <span className="text-base font-semibold">Clock Manager</span>
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
      <main className="mx-auto w-full max-w-3xl flex-1 px-4 py-8">{children}</main>
      <footer className="mx-auto w-full max-w-3xl px-4 pb-6 text-sm text-muted">
        <a className="underline" href={href('/time-lab')}>
          Time lab (developer tool)
        </a>
      </footer>
    </div>
  )
}
