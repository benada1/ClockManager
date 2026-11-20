import Banner from '../../components/Banner.jsx'
import Button from '../../components/Button.jsx'
import Card from '../../components/Card.jsx'
import { PinIcon, PlayIcon, StopIcon } from '../../components/icons.jsx'
import { StatusPill } from '../../components/Pill.jsx'
import { elapsedSince, zurichClock } from '../../lib/serverClock.js'
import { formatDuration, needsOffset, zoneLabel, zurichDay, zurichTime } from '../../lib/zurich.js'
import { formatElapsed, spokenElapsed } from './elapsedText.js'
import { useServerNow } from './useServerClock.js'

/** The big HH:MM with small :SS. Ticks every second, so it is hidden from screen readers; they get the minute. */
function BigClock({ clock }) {
  return (
    <>
      <p aria-hidden="true" className="flex items-baseline gap-1.5 tabular-nums">
        <span className="text-7xl leading-none font-semibold tracking-tighter sm:text-8xl">{clock.hm}</span>
        <span className="text-2xl font-medium tracking-tight text-muted sm:text-3xl">:{clock.ss}</span>
      </p>
      <p className="sr-only">
        Zurich time from the server: {clock.hm}
        {clock.dstDay ? ` (${clock.offsetLabel})` : ''}
      </p>
    </>
  )
}

function NoServerTime() {
  return (
    <div className="flex flex-col gap-1">
      <p className="text-3xl font-semibold tracking-tight text-ink-3">Server time unavailable</p>
      <p className="text-base text-muted">
        We can't get a trusted time right now, so no clock is shown. You can still try to punch: the server only records
        it when it gets a trusted time.
      </p>
    </div>
  )
}

/** One line under the clock: what state you are in and what just happened. */
function StateLine({ status, justClosedId, now, today }) {
  const open = status.openShift
  if (open) {
    const t = open.clockIn.zurich
    const ms = elapsedSince(now, open.clockIn.utc ?? t)
    const otherDay = !today || t?.slice(0, 10) !== today
    return (
      <p className="text-base text-ink-2">
        <span aria-live="polite">
          Shift started at <strong className="font-semibold text-ink">{zurichTime(t) ?? '–'}</strong>
          {needsOffset(t) ? ` ${zoneLabel(t, true)}` : ''}
          {otherDay && zurichDay(t) ? `, ${zurichDay(t)}` : ''}
        </span>
        {ms !== null && (
          <>
            <span aria-hidden="true">
              {' '}
              · running for <strong className="font-semibold text-ink tabular-nums">{formatElapsed(ms)}</strong>
            </span>
            <span className="sr-only">, running for {spokenElapsed(ms)}</span>
          </>
        )}
      </p>
    )
  }

  const last = status.lastClosedShift
  if (last && last.shiftId === justClosedId) {
    const tOut = last.clockOut?.zurich
    const worked = formatDuration(last.durationSeconds)
    return (
      <div aria-live="polite" className="flex flex-col items-start gap-2">
        <p className="rounded-full bg-ok-soft px-3 py-1 text-sm font-semibold text-ok">
          <span className="sr-only">Done: </span>Clocked out and saved
        </p>
        <p className="text-base text-ink-2">
          You clocked out at <strong className="font-semibold text-ink">{zurichTime(tOut) ?? '–'}</strong>{' '}
          {zoneLabel(tOut, needsOffset(last.clockIn?.zurich, tOut))}
          {worked && <> · worked <strong className="font-semibold text-ink">{worked}</strong></>}
        </p>
      </div>
    )
  }

  return (
    <p aria-live="polite" className="text-base text-ink-3">
      Ready when you are. Press the button below when you start work.
    </p>
  )
}

/** The main card: state pill, the live server clock, the result line, and the one punch button. */
export default function ClockCard({ status, anchor, busy, stuck, problem, justClosedId, onPress }) {
  const clockedIn = Boolean(status.openShift)
  const now = useServerNow(anchor.anchor)
  const clock = now === null ? null : zurichClock(now)
  const synced = anchor.anchor === null ? null : zurichClock(anchor.anchor + anchor.syncAt)

  return (
    <Card aria-labelledby="punch-h" className="flex flex-col gap-8 p-6 sm:p-10">
      <h2 id="punch-h" className="sr-only">
        Clock in and out
      </h2>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <StatusPill on={clockedIn}>{clockedIn ? 'On shift' : 'Not clocked in'}</StatusPill>
        <span className="inline-flex items-center gap-1.5 text-sm text-muted">
          <PinIcon />
          Zurich time{clock?.dstDay ? `, ${clock.offsetLabel}` : ''} · from the server
        </span>
      </div>

      <div className="flex flex-col gap-3">
        {anchor.anchor === null ? (
          <NoServerTime />
        ) : clock ? (
          <BigClock clock={clock} />
        ) : (
          <p aria-hidden="true" className="text-7xl leading-none sm:text-8xl">&nbsp;</p>
        )}
        <StateLine status={status} justClosedId={justClosedId} now={now} today={clock?.date ?? synced?.date} />
      </div>

      <div className="mt-auto flex flex-col gap-3">
        {problem && (
          <Banner
            tone={problem.tone ?? 'error'}
            title={problem.title}
            action={
              problem.retry && (
                <Button variant="secondary" busy={busy} busyLabel="Trying again…" onClick={onPress}>
                  Try again
                </Button>
              )
            }
          >
            {problem.text}
          </Banner>
        )}
        <Button
          size="xl"
          block
          variant={clockedIn ? 'strong' : 'primary'}
          busy={busy}
          busyLabel={clockedIn ? 'Clocking out…' : 'Clocking in…'}
          disabled={stuck}
          onClick={onPress}
          className="gap-2.5 hover:shadow-cta active:scale-[0.99]"
        >
          {clockedIn ? <StopIcon /> : <PlayIcon />}
          {clockedIn ? 'Clock out' : 'Clock in'}
        </Button>
        {problem?.retry && <p className="text-center text-sm text-ink-2">Use "Try again" above to send the same punch.</p>}
        <p className="text-center text-sm text-muted">Times are recorded by the server, not your device.</p>
      </div>
    </Card>
  )
}
