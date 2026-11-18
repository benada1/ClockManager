import { formatDuration, needsOffset, zoneLabel, zurichDay, zurichTime } from '../../lib/zurich.js'
import useElapsed, { formatElapsed, spokenElapsed } from './useElapsed.js'

const KEY = 'text-2xl font-semibold md:text-3xl'

/** Clock out block while clocked in: ticking time since clock-in (server time) and the start time. */
export function ClockedInFor({ status, syncAt }) {
  const t = status.openShift.clockIn.zurich
  const ms = useElapsed(status, syncAt)
  return (
    <>
      {ms !== null && (
        <div className="flex flex-col gap-1">
          <p className="text-base">Clocked in for</p>
          {/* Ticks every second: hidden from screen readers, which get the once-a-minute text. */}
          <p aria-hidden="true" className="text-5xl font-semibold tabular-nums md:text-6xl">
            {formatElapsed(ms)}
          </p>
          <p className="sr-only">Clocked in for {spokenElapsed(ms)}</p>
          <p className="text-sm">Counted from the time we got from the server</p>
        </div>
      )}
      <div aria-live="polite" className="flex flex-col gap-1">
        <p className="text-xl">
          Since <strong>{zurichTime(t) ?? '–'}</strong> {zoneLabel(t, needsOffset(t))}
        </p>
        <p className="text-base">{zurichDay(t)}</p>
      </div>
    </>
  )
}

/** Clock out block while clocked out: the last result, or a calm "not clocked in". */
export function LastResult({ status, justClosedId }) {
  const last = status.lastClosedShift
  if (!last) {
    return (
      <p aria-live="polite" className={KEY}>
        You are not clocked in
      </p>
    )
  }
  const tIn = last.clockIn.zurich
  const tOut = last.clockOut?.zurich
  const label = zoneLabel(tOut, needsOffset(tIn, tOut))
  const worked = formatDuration(last.durationSeconds)
  const fresh = last.shiftId === justClosedId

  return (
    <div aria-live="polite" className="flex flex-col gap-2">
      {fresh && (
        <p className="w-fit rounded-ui border border-ok-line bg-ok-soft px-3 py-1 text-base font-semibold text-ok">
          <span className="sr-only">Done: </span>Clocked out and saved
        </p>
      )}
      <p className={`${KEY} text-ink`}>
        {fresh ? 'You clocked out at' : 'Last clock out:'} <strong>{zurichTime(tOut) ?? '–'}</strong> {label}
      </p>
      {worked && <p className="text-xl text-ink">Worked {worked}</p>}
      <p className="text-base">
        Started at {zurichTime(tIn) ?? '–'}, {zurichDay(tIn)}
      </p>
    </div>
  )
}

/** Clock in block while clocked in: when the shift started. */
export function ClockedInAt({ status }) {
  const t = status.openShift.clockIn.zurich
  return (
    <div aria-live="polite" className="flex flex-col gap-1">
      <p className={`${KEY} text-ink`}>
        You clocked in at <strong>{zurichTime(t) ?? '–'}</strong> {zoneLabel(t, needsOffset(t))}
      </p>
      <p className="text-base">{zurichDay(t)}</p>
    </div>
  )
}

/** Clock in block while clocked out. */
export function ReadyToStart() {
  return <p className={KEY}>Start your shift when you begin work.</p>
}
