import Card from '../../components/Card.jsx'
import Pill from '../../components/Pill.jsx'
import { dayNoteTexts, formatTotal, shiftBadges, shiftDurationText, shiftTimesText } from './labels.js'
import { formatDayHeading } from './weeks.js'

function ShiftRow({ shift, manager, action }) {
  const badges = shiftBadges(shift, { manager })
  return (
    <li className="flex flex-col gap-2 py-4 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <p className="text-base font-medium">{shiftTimesText(shift)}</p>
        <div className="flex items-center gap-4">
          <p className={shift.open ? 'text-sm text-muted' : 'text-base font-semibold'}>
            <span className="sr-only">Duration: </span>
            {shiftDurationText(shift, { manager })}
          </p>
          {action}
        </div>
      </div>
      {badges.length > 0 && (
        <ul className="m-0 flex list-none flex-wrap gap-2 p-0">
          {badges.map((b) => (
            <Pill key={b.key} tone={b.tone}>
              {b.text}
            </Pill>
          ))}
        </ul>
      )}
    </li>
  )
}

/** One day with shifts: weekday and date, the day total, notes, the shifts. `renderAction(shift)` adds a control per shift. */
export default function DayCard({ day, manager = false, renderAction }) {
  const notes = dayNoteTexts(day)
  const counted = day.shifts.some((s) => !s.open)
  return (
    <li>
      <Card as="div" className="flex flex-col gap-4 p-4 md:p-6">
        <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
          <h3 className="m-0 text-lg font-semibold">{formatDayHeading(day.date)}</h3>
          <p className="text-base text-muted">
            Day total: <span className="font-semibold text-ink">{counted ? formatTotal(day.totalSeconds) : 'nothing counted yet'}</span>
          </p>
        </div>
        {notes.length > 0 && (
          <ul className="m-0 flex list-none flex-wrap gap-2 p-0">
            {notes.map((n) => (
              <Pill key={n}>{n}</Pill>
            ))}
          </ul>
        )}
        <ul className="m-0 flex list-none flex-col divide-y divide-line p-0">
          {day.shifts.map((s) => (
            <ShiftRow key={s.shiftId} shift={s} manager={manager} action={renderAction?.(s)} />
          ))}
        </ul>
      </Card>
    </li>
  )
}
