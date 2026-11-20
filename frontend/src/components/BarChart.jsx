import { useEffect, useRef, useState } from 'react'
import { dayBarParts } from '../pages/history/labels.js'
import { nextColumn, tipAlign } from './barChartNav.js'

const ALIGN = { start: 'left-0', end: 'right-0', center: 'left-1/2 -translate-x-1/2' }

/**
 * Hours per weekday as plain divs. `days` come from weekDays(); `highlight(day)` makes a label bold.
 * Each day column is one image whose name equals its tooltip; one tab stop, arrows move between days (D-088).
 */
export default function BarChart({ days, short = false, highlight = () => false, className = '' }) {
  const max = Math.max(0, ...days.map((d) => d.seconds))
  const [hover, setHover] = useState(null)
  const [focus, setFocus] = useState(null)
  const [stop, setStop] = useState(null)
  const [dismissed, setDismissed] = useState(false)
  const root = useRef(null)
  const cols = useRef([])

  const tabStop = stop !== null && stop < days.length ? stop : Math.max(0, days.findIndex(highlight))
  const shown = dismissed ? null : (hover ?? focus)

  // Escape anywhere or a tap outside hides the tooltip (WCAG 1.4.13 dismissible).
  useEffect(() => {
    if (shown === null) return undefined
    const onKey = (e) => e.key === 'Escape' && setDismissed(true)
    const onDown = (e) => !root.current?.contains(e.target) && setDismissed(true)
    document.addEventListener('keydown', onKey)
    document.addEventListener('pointerdown', onDown)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('pointerdown', onDown)
    }
  }, [shown])

  const show = (set, i) => {
    set(i)
    setDismissed(false)
  }

  return (
    <div ref={root} role="group" aria-label="Hours per day" className={`grid grid-cols-7 gap-2 ${className}`}>
      {days.map((d, i) => {
        const parts = dayBarParts(d)
        const on = shown === i
        const strong = d.seconds > 0 || highlight(d) || on
        const bar = d.seconds > 0 ? `min-h-1.5 rounded-md ${on ? 'bg-accent-strong' : 'bg-accent'}` : `h-1 rounded ${on ? 'bg-faint' : 'bg-bar-empty'}`
        return (
          <div
            key={d.date}
            ref={(el) => {
              cols.current[i] = el
            }}
            role="img"
            aria-label={parts.join(' · ')}
            tabIndex={i === tabStop ? 0 : -1}
            className="flex h-full cursor-default flex-col items-center gap-1.5 rounded-md outline-offset-4"
            onPointerEnter={() => show(setHover, i)}
            onPointerLeave={() => setHover((h) => (h === i ? null : h))}
            onFocus={() => {
              setStop(i)
              show(setFocus, i)
            }}
            onBlur={() => setFocus((f) => (f === i ? null : f))}
            onClick={() => show(setFocus, i)}
            onKeyDown={(e) => {
              const j = nextColumn(e.key, i, days.length)
              if (j === null) return
              e.preventDefault()
              cols.current[j]?.focus()
            }}
          >
            <div className="flex w-full flex-1 items-end">
              <span
                className={`relative w-full transition-colors ${bar}`}
                style={d.seconds > 0 ? { height: `${Math.max(6, Math.round((d.seconds / max) * 100))}%` } : undefined}
              >
                {on && <Tip parts={parts} align={tipAlign(i, days.length)} />}
              </span>
            </div>
            <span aria-hidden="true" className={`text-xs ${strong ? 'font-semibold text-ink' : 'text-muted'}`}>
              {short ? d.letter : d.short}
            </span>
          </div>
        )
      })}
    </div>
  )
}

/** The small card above the bar; hidden from screen readers because the column's name says the same. */
function Tip({ parts, align }) {
  const [name, ...rest] = parts
  return (
    <span
      aria-hidden="true"
      data-chart-tip=""
      className={`absolute bottom-full z-10 mb-1.5 w-max rounded-control border border-control bg-surface px-3 py-2 text-left shadow-pop motion-safe:animate-tip-in ${ALIGN[align]}`}
    >
      <span className="block text-xs font-medium text-muted">{name}</span>
      <span className="block text-sm font-semibold whitespace-nowrap text-ink tabular-nums">{rest.join(' · ')}</span>
    </span>
  )
}
