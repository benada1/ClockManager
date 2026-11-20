import EmptyState from '../components/EmptyState.jsx'
import { href } from '../router.js'
import useHistory from './history/useHistory.js'
import WeekView from './history/WeekView.jsx'

export default function History() {
  const history = useHistory()

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <WeekView
        history={history}
        loadingText="Loading your shifts…"
        header={
          <div className="flex flex-col gap-1">
            <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight">
              History
            </h1>
            <p className="text-base text-muted">Your shifts, week by week.</p>
          </div>
        }
        empty={
          <EmptyState
            title="No shifts yet"
            action={
              <a className="inline-flex min-h-11 items-center text-sm font-semibold text-accent-ink no-underline hover:underline" href={href('/')}>
                Go to the clock
              </a>
            }
          >
            Clock in and your shifts will show up here.
          </EmptyState>
        }
      />

      <p className="text-sm text-muted">All times are Zurich time and come from the server. Hours are counted from the exact moments.</p>
    </div>
  )
}
