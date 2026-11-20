import BarChart from '../../components/BarChart.jsx'
import Banner from '../../components/Banner.jsx'
import Button from '../../components/Button.jsx'
import Card from '../../components/Card.jsx'
import { ChevronLeft, ChevronRight } from '../../components/icons.jsx'
import Loading from '../../components/Loading.jsx'
import DayCard from './DayCard.jsx'
import { totalParts, weekCountsText } from './labels.js'
import { formatRange, weekDays } from './weeks.js'

const NO_RETRY = new Set(['invalid', 'forbidden', 'notfound'])

function WeekNav({ data, loading, previous, next }) {
  return (
    <div className="flex items-center gap-2">
      <Button variant="secondary" size="icon" disabled={loading} onClick={previous} aria-label="Previous week">
        <ChevronLeft />
      </Button>
      <span aria-live="polite" className="min-w-40 text-center text-base font-semibold tabular-nums">
        {formatRange(data.from, data.to)}
      </span>
      <Button variant="secondary" size="icon" disabled={loading} onClick={next} aria-label="Next week">
        <ChevronRight />
      </Button>
    </div>
  )
}

function Summary({ data, hasOpen }) {
  const total = totalParts(data.totalSeconds)
  return (
    <Card aria-label="Week summary" className="flex flex-wrap items-center gap-8 px-6 py-6 sm:px-7">
      <div className="flex min-w-44 flex-col gap-1">
        <span className="text-sm font-medium text-muted">Week total</span>
        <span className="text-3xl font-semibold tracking-tight tabular-nums">
          {total.h}
          <span className="text-lg font-medium text-muted"> h </span>
          {total.m}
          <span className="text-lg font-medium text-muted"> min</span>
        </span>
        <span className="text-sm text-muted">
          {weekCountsText(data)}
          {hasOpen && ' · open shifts are not counted'}
        </span>
      </div>
      <BarChart days={weekDays(data)} className="h-24 min-w-0 flex-1 basis-72" />
    </Card>
  )
}

/**
 * The week screen shared by History and the manager's employee view: page header with the week navigation,
 * loading, failure, empty, summary and days.
 */
export default function WeekView({ history, header, empty, loadingText, manager = false, renderAction }) {
  const { data, loading, failure, previous, next, retry } = history
  const hasWeek = Boolean(data?.from && data?.to)
  const hasOpen = data?.days?.some((d) => d.shifts.some((s) => s.open))

  return (
    <>
      <div className="flex flex-wrap items-end justify-between gap-4">
        {header}
        {hasWeek && <WeekNav data={data} loading={loading} previous={previous} next={next} />}
      </div>

      {!data && loading && <Loading>{loadingText}</Loading>}

      {failure && (
        <Banner
          tone="error"
          title={failure.title}
          action={
            !NO_RETRY.has(failure.kind) && (
              <Button variant="secondary" busy={loading} busyLabel="Trying again…" onClick={retry}>
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
          <Summary data={data} hasOpen={hasOpen} />

          <p role="status" className="sr-only">
            {loading ? 'Loading this week…' : ''}
          </p>

          {data.days.length === 0 ? (
            <p className="rounded-card border border-dashed border-line px-6 py-10 text-center text-base text-muted">No shifts this week.</p>
          ) : (
            <ul aria-busy={loading} className={`flex list-none flex-col gap-6 p-0 transition-opacity ${loading ? 'opacity-60' : ''}`}>
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
