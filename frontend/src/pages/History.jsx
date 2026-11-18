import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card from '../components/Card.jsx'
import Loading from '../components/Loading.jsx'
import { href } from '../router.js'
import DayCard from './history/DayCard.jsx'
import { formatTotal } from './history/labels.js'
import useHistory from './history/useHistory.js'
import { formatRange } from './history/weeks.js'

export default function History() {
  const { data, loading, failure, previous, next, retry } = useHistory()
  const hasWeek = Boolean(data?.from && data?.to)
  const hasOpen = data?.days?.some((d) => d.shifts.some((s) => s.open))

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <h1 tabIndex={-1} className="text-2xl font-semibold">
        History
      </h1>

      {!data && loading && <Loading>Loading your shifts…</Loading>}

      {failure && (
        <Banner
          tone="error"
          title={failure.title}
          action={
            failure.kind !== 'invalid' && (
              <Button variant="secondary" busy={loading} busyLabel="Trying again…" onClick={retry} className="self-start">
                Try again
              </Button>
            )
          }
        >
          {data ? `${failure.text} The week below is the last one we loaded.` : failure.text}
        </Banner>
      )}

      {data && !hasWeek && (
        <Card className="flex flex-col items-start gap-3">
          <p className="text-lg font-medium">You have no shifts yet.</p>
          <p className="text-muted">Clock in and your shifts will show up here.</p>
          <a className="font-medium text-accent underline" href={href('/')}>
            Go to the clock
          </a>
        </Card>
      )}

      {hasWeek && (
        <>
          <Card className="flex flex-col gap-4 p-4 md:flex-row md:items-center md:justify-between md:p-6">
            <div className="flex flex-col gap-1">
              <h2 className="text-xl font-semibold">{formatRange(data.from, data.to)}</h2>
              <p className="text-base text-muted">
                Total this week: <span className="font-semibold text-ink">{formatTotal(data.totalSeconds)}</span>
                {hasOpen && ' (open shifts are not counted)'}
              </p>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Button variant="secondary" disabled={loading} onClick={previous}>
                <span aria-hidden="true">&larr;</span> Previous week
              </Button>
              <Button variant="secondary" disabled={loading} onClick={next}>
                Next week <span aria-hidden="true">&rarr;</span>
              </Button>
            </div>
          </Card>

          <p role="status" className="sr-only">
            {loading ? 'Loading this week…' : ''}
          </p>

          {data.days.length === 0 ? (
            <p className="py-6 text-center text-lg text-muted">No shifts this week.</p>
          ) : (
            <ul aria-busy={loading} className={`m-0 flex list-none flex-col gap-4 p-0 ${loading ? 'opacity-60' : ''}`}>
              {data.days.map((day) => (
                <DayCard key={day.date} day={day} />
              ))}
            </ul>
          )}
        </>
      )}

      <p className="text-sm text-muted">All times are Zurich time and come from the server. Hours are counted from the exact moments.</p>
    </div>
  )
}
