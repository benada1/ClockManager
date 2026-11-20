import { useEffect, useRef } from 'react'
import { getShifts } from '../../api/history.js'
import BarChart from '../../components/BarChart.jsx'
import Button from '../../components/Button.jsx'
import Card, { CardTitle } from '../../components/Card.jsx'
import { href } from '../../router.js'
import { totalParts } from '../history/labels.js'
import { formatShortRange, weekDays } from '../history/weeks.js'
import useLoad from '../manager/useLoad.js'

/**
 * Hours of the current Zurich week (`range` from the server's date) or, without a server date, the server's
 * default week. Mount it with key={range?.from} so a new week starts fresh; `version` changes after a punch.
 */
export default function WeekCard({ range, today, version }) {
  const week = useLoad((signal) => getShifts(range, signal))
  const { data, loading, failure, reload } = week
  const seen = useRef(version)

  useEffect(() => {
    if (seen.current === version) return
    seen.current = version
    reload()
  }, [version, reload])

  const total = totalParts(data?.totalSeconds)
  const hasOpen = data?.days?.some((d) => d.shifts.some((s) => s.open))

  return (
    <Card aria-labelledby="week-h" className="flex flex-1 flex-col gap-4">
      <CardTitle id="week-h" note={data?.from ? formatShortRange(data.from, data.to) : null}>
        {range ? 'This week' : 'Latest week'}
      </CardTitle>

      {!data && loading && <p role="status" className="text-sm text-muted">Loading your week…</p>}
      {failure && (
        <div role="alert" className="flex flex-col items-start gap-2 text-sm text-ink-2">
          <p>{data ? 'We could not refresh your week. These are the last hours we loaded.' : 'We could not load your week.'}</p>
          <Button variant="quiet" busy={loading} busyLabel="Trying again…" onClick={reload} className="-ml-4">
            Try again
          </Button>
        </div>
      )}

      {data && !data.from && <p className="text-base text-muted">No shifts yet.</p>}
      {data?.from && (
        <>
          <p className="text-3xl font-semibold tracking-tight tabular-nums">
            {total.h}
            <span className="text-lg font-medium text-muted"> h </span>
            {total.m}
            <span className="text-lg font-medium text-muted"> min</span>
            {hasOpen && <span className="block text-sm font-normal tracking-normal text-muted">The open shift is counted once you clock out.</span>}
          </p>
          <BarChart days={weekDays(data)} short highlight={(d) => d.date === today} className="h-24" />
        </>
      )}

      <a href={href('/history')} className="mt-auto inline-flex min-h-11 items-center gap-1.5 self-start text-sm font-semibold text-accent-ink no-underline hover:underline">
        View history <span aria-hidden="true">→</span>
      </a>
    </Card>
  )
}
