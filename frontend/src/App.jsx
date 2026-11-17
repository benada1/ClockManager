import { useEffect, useRef } from 'react'
import { href, useHashRoute } from './router.js'
import Home from './pages/Home.jsx'
import TimeLab from './pages/TimeLab.jsx'

const ROUTES = [
  { path: '/', label: 'Home', component: Home },
  { path: '/time-lab', label: 'Time lab', component: TimeLab },
]

function NotFound() {
  return (
    <>
      <h1 tabIndex={-1}>Page not found</h1>
      <p>
        <a href={href('/')}>Go to the home page</a>
      </p>
    </>
  )
}

function App() {
  const path = useHashRoute()
  const route = ROUTES.find((r) => r.path === path)
  const Page = route?.component ?? NotFound
  const mainRef = useRef(null)
  const firstRender = useRef(true)

  // After client-side navigation, move focus to the new page's heading so keyboard
  // and screen-reader users land at the start of the new content.
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false
      return
    }
    mainRef.current?.querySelector('h1')?.focus()
  }, [path])

  return (
    <>
      <header className="app-header">
        <span className="app-name">Clock Manager</span>
        <nav aria-label="Main">
          <ul>
            {ROUTES.map((r) => (
              <li key={r.path}>
                <a href={href(r.path)} aria-current={r.path === path ? 'page' : undefined}>
                  {r.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
      </header>
      <main ref={mainRef} className="page">
        <Page />
      </main>
    </>
  )
}

export default App
