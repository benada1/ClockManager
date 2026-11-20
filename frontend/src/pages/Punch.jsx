import { useAuth } from '../auth/authContext.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Loading from '../components/Loading.jsx'
import { greetingFor, zurichClock } from '../lib/serverClock.js'
import { weekOf } from './history/weeks.js'
import ClockCard from './punch/ClockCard.jsx'
import LastShiftCard from './punch/LastShiftCard.jsx'
import ShiftNotices from './punch/ShiftNotices.jsx'
import usePunch from './punch/usePunch.js'
import { useServerAnchor, useServerNow } from './punch/useServerClock.js'
import WeekCard from './punch/WeekCard.jsx'

/** "Good afternoon, ben" and the date, from server time. Without it: "Hello, ben" and no date. */
function Greeting({ name, anchor }) {
  const clock = zurichClock(useServerNow(anchor))
  return (
    <div className="flex flex-col gap-1">
      <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight wrap-anywhere">
        {clock ? greetingFor(clock.hour) : 'Hello'}
        {name ? `, ${name}` : ''}
      </h1>
      <p className="min-h-6 text-base text-muted">{clock?.dateLabel}</p>
    </div>
  )
}

export default function Punch() {
  const { user } = useAuth()
  const { status, syncAt, load, busy, problem, justClosedId, refreshFailed, press, refresh } = usePunch()
  const anchor = useServerAnchor(status, syncAt)
  const name = status?.employee?.displayName ?? user?.displayName
  const stuck = load === 'offline' || Boolean(problem?.retry) // use the banner's "Try again" instead
  // The week of the server's Zurich date at the last sync; null without trusted time.
  const today = anchor.anchor === null ? null : zurichClock(anchor.anchor + anchor.syncAt)?.date ?? null
  const range = today ? weekOf(today) : null
  const version = `${status?.openShift?.shiftId ?? ''}|${status?.lastClosedShift?.shiftId ?? ''}`

  return (
    <div className="flex w-full flex-col gap-6">
      <Greeting name={name} anchor={anchor.anchor} />

      {load === 'loading' && !status && <Loading>Loading your status…</Loading>}

      {load === 'error' && !status && (
        <Banner tone="error" title="We could not load your status." action={<Button variant="secondary" onClick={refresh}>Try again</Button>} />
      )}

      {load === 'inactive' && (
        <Banner tone="error" title="This account is switched off.">
          Ask your manager to switch it on again.
        </Banner>
      )}

      {status && load !== 'inactive' && (
        <>
          <ShiftNotices status={status} />

          {load === 'offline' && (
            <Banner tone="error" title="You are offline or the server is down." action={<Button variant="secondary" onClick={refresh}>Check again</Button>}>
              What you see may be out of date. You can clock in or out once you are back online.
            </Banner>
          )}

          {refreshFailed && load !== 'offline' && (
            <Banner tone="warning" title="We could not refresh this page." action={<Button variant="secondary" onClick={refresh}>Try again</Button>}>
              What you see is the last answer we got from the server.
            </Banner>
          )}

          <div className="grid items-stretch gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
            <ClockCard
              status={status}
              anchor={anchor}
              busy={busy}
              stuck={stuck}
              problem={problem}
              justClosedId={justClosedId}
              onPress={press}
            />
            <div className="flex min-w-0 flex-col gap-6">
              <LastShiftCard status={status} />
              <WeekCard key={range?.from ?? 'latest'} range={range} today={today} version={version} />
            </div>
          </div>
        </>
      )}
    </div>
  )
}
