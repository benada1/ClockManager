import Card from '../components/Card.jsx'
import { href } from '../router.js'
import useHistory from './history/useHistory.js'
import WeekView from './history/WeekView.jsx'

export default function History() {
  const history = useHistory()

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <h1 tabIndex={-1} className="text-2xl font-semibold">
        History
      </h1>

      <WeekView
        history={history}
        loadingText="Loading your shifts…"
        empty={
          <Card className="flex flex-col items-start gap-3">
            <p className="text-lg font-medium">You have no shifts yet.</p>
            <p className="text-muted">Clock in and your shifts will show up here.</p>
            <a className="font-medium text-accent underline" href={href('/')}>
              Go to the clock
            </a>
          </Card>
        }
      />

      <p className="text-sm text-muted">All times are Zurich time and come from the server. Hours are counted from the exact moments.</p>
    </div>
  )
}
