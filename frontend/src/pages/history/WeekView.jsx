import Banner from '../../components/Banner.jsx'
import Button from '../../components/Button.jsx'
import Card from '../../components/Card.jsx'
import Loading from '../../components/Loading.jsx'
import DayCard from './DayCard.jsx'
import { formatTotal } from './labels.js'
import { formatRange } from './weeks.js'

const NO_RETRY = new Set(['invalid', 'forbidden', 'notfound'])

/** The week screen shared by History and the manager's employee view: loading, failure, empty, week + days. */
export default function WeekView({ history, empty, loadingText, manager = false, renderAction }) {
  const { data, loading, failure, previous, next, retry } = history
  const hasWeek = Boolean(data?.from && data?.to)
  const hasOpen = data?.days?.some((d) => d.shifts.some((s) => s.open))

  return (
    <>
      {!data && loading && <Loading>{loadingText}</Loading>}

      {failure && (
        <Banner
          tone="error"
          title={failure.title}
          action={
            !NO_RETRY.has(failure.kind) && (
              <Button variant="secondary" busy={loading} busyLabel="Trying again…" onClick={retry} className="self-start">
                Try again
              </Button>
            )
          }
        >
          {data ? `${failure.text} The week below is the last one we loaded.` : failure.text}
        </Banner>
      )}

      {data && !hasWeek && empty}

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
                <DayCard key={day.date} day={day} manager={manager} renderAction={renderAction} />
              ))}
            </ul>
          )}
        </>
      )}
    </>
  )
}
