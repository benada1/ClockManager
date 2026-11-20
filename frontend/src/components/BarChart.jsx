import { formatDuration } from '../lib/zurich.js'

/**
 * Hours per weekday as plain divs. `days` come from weekDays(); `highlight(day)` makes a label bold.
 * One spoken summary for screen readers; the bars themselves are hidden from them.
 */
export default function BarChart({ days, short = false, highlight = () => false, className = '' }) {
  const max = Math.max(0, ...days.map((d) => d.seconds))
  const spoken = days.map((d) => `${d.name} ${d.seconds > 0 ? formatDuration(d.seconds) : 'none'}`).join(', ')

  return (
    <div role="img" aria-label={`Hours per day: ${spoken}`} className={`grid grid-cols-7 gap-2 ${className}`}>
      {days.map((d) => {
        const strong = d.seconds > 0 || highlight(d)
        return (
          <div key={d.date} aria-hidden="true" className="flex h-full flex-col items-center gap-1.5">
            <div className="flex w-full flex-1 items-end">
              <span
                className={`w-full ${d.seconds > 0 ? 'min-h-1.5 rounded-md bg-accent' : 'h-1 rounded bg-bar-empty'}`}
                style={d.seconds > 0 ? { height: `${Math.max(6, Math.round((d.seconds / max) * 100))}%` } : undefined}
              />
            </div>
            <span className={`text-xs ${strong ? 'font-semibold text-ink' : 'text-muted'}`}>{short ? d.letter : d.short}</span>
          </div>
        )
      })}
    </div>
  )
}
