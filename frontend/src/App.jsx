import { useEffect, useRef } from 'react'
import { useAuth } from './auth/authContext.js'
import AppShell from './components/AppShell.jsx'
import Banner from './components/Banner.jsx'
import Button from './components/Button.jsx'
import Loading from './components/Loading.jsx'
import History from './pages/History.jsx'
import Login from './pages/Login.jsx'
import Manager from './pages/Manager.jsx'
import ManagerEmployee from './pages/ManagerEmployee.jsx'
import ManagerShift from './pages/ManagerShift.jsx'
import Punch from './pages/Punch.jsx'
import Signup from './pages/Signup.jsx'
import TimeLab from './pages/TimeLab.jsx'
import { href, navigate, useHashRoute } from './router.js'

const GUEST_PAGES = { '/login': Login, '/signup': Signup }
const LEGACY_ROUTES = [
  { path: '/', label: 'Clock' },
  { path: '/time-lab', label: 'Time lab' },
]

// #/manager, #/manager/employee/<id>, #/manager/shift/<id>. Keyed by id so a new id starts fresh.
function managerPage(path) {
  if (path === '/manager') return <Manager />
  const m = /^\/manager\/(employee|shift)\/([^/]+)$/.exec(path)
  if (!m) return null
  let id
  try {
    id = decodeURIComponent(m[2])
  } catch {
    return null
  }
  return m[1] === 'employee' ? <ManagerEmployee key={id} employeeId={id} /> : <ManagerShift key={id} shiftId={id} />
}

function NotFound() {
  return (
    <div className="mx-auto my-auto flex max-w-md flex-col items-center gap-3 text-center">
      <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight">
        Page not found
      </h1>
      <p className="text-base text-muted">This address does not lead anywhere in Clock Manager.</p>
      <a className="font-semibold text-accent-ink no-underline hover:underline" href={href('/')}>
        Go to the start page
      </a>
    </div>
  )
}

// Time lab keeps its own plain-CSS shell (developer tool, no login needed).
function TimeLabShell({ path }) {
  return (
    <>
      <header className="app-header">
        <span className="app-name">Clock Manager</span>
        <nav aria-label="Main">
          <ul>
            {LEGACY_ROUTES.map((r) => (
              <li key={r.path}>
                <a href={href(r.path)} aria-current={r.path === path ? 'page' : undefined}>
                  {r.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
      </header>
      <main className="page">
        <TimeLab />
      </main>
    </>
  )
}

function App() {
  const path = useHashRoute()
  const { phase, retry } = useAuth()
  const mainRef = useRef(null)
  const firstRender = useRef(true)

  const guest = path in GUEST_PAGES
  const signedInPage = path === '/' || path === '/history' || path === '/manager' || path.startsWith('/manager/')
  const redirectTo = phase === 'in' && guest ? '/' : phase === 'out' && signedInPage ? '/login' : null

  useEffect(() => {
    if (redirectTo) navigate(redirectTo)
  }, [redirectTo])

  // After navigation, move focus to the new page's heading for keyboard and screen-reader users.
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false
      return
    }
    mainRef.current?.querySelector('h1')?.focus()
  }, [path, phase])

  if (path === '/time-lab') return <TimeLabShell path={path} />

  let content
  if (phase === 'loading') content = <Loading>Loading…</Loading>
  else if (phase === 'unreachable')
    content = (
      <div className="mx-auto my-auto w-full max-w-md">
        <Banner
          tone="error"
          title="You are offline or the server is down."
          action={
            <Button variant="secondary" onClick={retry}>
              Try again
            </Button>
          }
        >
          We could not check whether you are logged in.
        </Banner>
      </div>
    )
  else if (redirectTo) content = null
  else if (guest) {
    const Page = GUEST_PAGES[path]
    content = <Page />
  } else if (path === '/') content = <Punch />
  else if (path === '/history') content = <History />
  else content = managerPage(path) ?? <NotFound />

  return (
    <div ref={mainRef}>
      <AppShell>{content}</AppShell>
    </div>
  )
}

export default App
