import Card, { CardTitle } from '../../components/Card.jsx'
import Pill from '../../components/Pill.jsx'
import { formatDuration, zurichDay } from '../../lib/zurich.js'
import { shiftBadges, shiftTimeParts } from '../history/labels.js'

function Point({ label, time, suffix, align = 'left' }) {
  return (
    <div className={`flex flex-col gap-0.5 ${align === 'right' ? 'items-end text-right' : ''}`}>
      <span className="text-xs text-muted">{label}</span>
      <span className="text-xl font-semibold tabular-nums">
        {time}
        {suffix && <span className="ml-1 text-xs font-medium text-muted">{suffix}</span>}
      </span>
    </div>
  )
}

/** The latest finished shift from the status: in, out, flags and length. */
export default function LastShiftCard({ status }) {
  const last = status.lastClosedShift
  if (!last) {
    return (
      <Card aria-labelledby="last-h" className="flex flex-col gap-3">
        <CardTitle id="last-h">Last shift</CardTitle>
        <p className="text-base text-muted">No finished shift yet. It shows up here after your first clock out.</p>
      </Card>
    )
  }
  const { start, end, endDay } = shiftTimeParts(last)
  const badges = shiftBadges(last)

  return (
    <Card aria-labelledby="last-h" className="flex flex-col gap-4">
      <CardTitle id="last-h" note={zurichDay(last.clockIn?.zurich)}>
        Last shift
      </CardTitle>
      <div className="flex items-center gap-3">
        <Point label="In" time={start} />
        <span aria-hidden="true" className="h-0 flex-1 border-t-2 border-dashed border-line" />
        <Point label="Out" time={end ?? '–'} suffix={endDay} align="right" />
      </div>
      {endDay && <p className="sr-only">Clocked out the next day, {endDay}.</p>}
      <div className="flex flex-wrap items-center justify-between gap-2 border-t border-subtle pt-4">
        {badges.length > 0 ? (
          <ul className="flex list-none flex-wrap gap-2 p-0">
            {badges.map((b) => (
              <Pill key={b.key} tone={b.tone}>
                {b.text}
              </Pill>
            ))}
          </ul>
        ) : (
          <span className="text-sm text-muted">Worked</span>
        )}
        <span className="text-sm font-semibold tabular-nums">
          <span className="sr-only">Duration: </span>
          {formatDuration(last.durationSeconds) ?? '–'}
        </span>
      </div>
    </Card>
  )
}
