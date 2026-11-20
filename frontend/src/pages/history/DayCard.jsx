import Pill from '../../components/Pill.jsx'
import { formatDuration } from '../../lib/zurich.js'
import { dayNoteTexts, shiftBadges, shiftDurationText, shiftTimeParts } from './labels.js'
import { formatDayHeading } from './weeks.js'

/** "08:03 → 17:12 Fri", read as "08:03 to 17:12, next day Fri". */
export function ShiftTimes({ shift }) {
  const { start, end, endDay } = shiftTimeParts(shift)
  if (!end) return <span>Since {start}</span>
  return (
    <span>
      {start}
      <span aria-hidden="true" className="text-faint"> → </span>
      <span className="sr-only"> to </span>
      {end}
      {endDay && (
        <span className="text-xs font-medium text-muted">
          {' '}
          <span className="sr-only">next day, </span>
          {endDay}
        </span>
      )}
    </span>
  )
}

function ShiftRow({ shift, manager, action }) {
  // "Ends next day" is already said by the weekday after the end time.
  const badges = shiftBadges(shift, { manager }).filter((b) => b.key !== 'next')
  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-subtle px-5 py-4 transition-colors first:border-t-0 hover:bg-subtle sm:px-7">
      <span aria-hidden="true" className={`size-2.5 shrink-0 rounded-full ${shift.open ? 'bg-ok-dot ring-4 ring-ok-dot/20' : 'bg-accent'}`} />
      <span className="min-w-36 text-base font-medium tabular-nums">
        <ShiftTimes shift={shift} />
      </span>
      {badges.length > 0 && (
        <ul className="flex list-none flex-wrap gap-2 p-0">
          {badges.map((b) => (
            <Pill key={b.key} tone={b.tone}>
              {b.text}
            </Pill>
          ))}
        </ul>
      )}
      <span className={`ml-auto tabular-nums ${shift.open ? 'text-sm text-muted' : 'text-base font-semibold'}`}>
        <span className="sr-only">Duration: </span>
        {shiftDurationText(shift, { manager })}
      </span>
      {action}
    </li>
  )
}

/** One day: header with weekday, shift count and total, then its shifts. `renderAction(shift)` adds a control per shift. */
export default function DayCard({ day, manager = false, renderAction }) {
  const notes = dayNoteTexts(day)
  const counted = day.shifts.some((s) => !s.open)
  const count = `${day.shifts.length} ${day.shifts.length === 1 ? 'shift' : 'shifts'}`
  return (
    <li className="overflow-hidden rounded-card border border-line bg-surface shadow-card">
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-line bg-subtle px-5 py-4 sm:px-7">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="text-base font-semibold">{formatDayHeading(day.date)}</h3>
          {notes.length > 0 && (
            <ul className="flex list-none flex-wrap gap-2 p-0">
              {notes.map((n) => (
                <Pill key={n} tone="accent">
                  {n}
                </Pill>
              ))}
            </ul>
          )}
        </div>
        <p className="text-sm text-muted">
          {count} ·{' '}
          <span className="sr-only">day total </span>
          <strong className="font-semibold text-ink tabular-nums">{counted ? formatDuration(day.totalSeconds) : 'nothing counted yet'}</strong>
        </p>
      </div>
      <ul className="flex list-none flex-col p-0">
        {day.shifts.map((s) => (
          <ShiftRow key={s.shiftId} shift={s} manager={manager} action={renderAction?.(s)} />
        ))}
      </ul>
    </li>
  )
}
