import { useAuth } from '../auth/authContext.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Loading from '../components/Loading.jsx'
import { ClockedInAt, ClockedInFor, LastResult, ReadyToStart } from './punch/BlockTexts.jsx'
import PunchBlock from './punch/PunchBlock.jsx'
import ShiftNotices from './punch/ShiftNotices.jsx'
import usePunch from './punch/usePunch.js'

export default function Punch() {
  const { user } = useAuth()
  const { status, syncAt, load, busy, problem, justClosedId, refreshFailed, press, refresh } = usePunch()
  const clockedIn = Boolean(status?.openShift)
  const name = status?.employee?.displayName ?? user?.displayName
  const stuck = load === 'offline' || Boolean(problem?.retry) // use the banner's "Try again" instead
  const retryHint = problem?.retry ? 'Use "Try again" above to send the same punch.' : null

  // Phone: the active block comes first. Desktop: Clock out left, Clock in right.
  const outBlock = (
    <PunchBlock
      key="out"
      label="Clock out"
      active={clockedIn}
      busy={busy}
      busyLabel="Clocking out…"
      disabled={stuck}
      onPress={press}
      reason={clockedIn ? retryHint : 'Not available: you are not clocked in'}
      className="md:order-1"
    >
      {status && (clockedIn ? <ClockedInFor status={status} syncAt={syncAt} /> : <LastResult status={status} justClosedId={justClosedId} />)}
    </PunchBlock>
  )
  const inBlock = (
    <PunchBlock
      key="in"
      label="Clock in"
      active={!clockedIn}
      busy={busy}
      busyLabel="Clocking in…"
      disabled={stuck}
      onPress={press}
      reason={clockedIn ? 'Not available: you are clocked in' : retryHint}
      className="md:order-2"
    >
      {status && (clockedIn ? <ClockedInAt status={status} /> : <ReadyToStart />)}
    </PunchBlock>
  )

  return (
    <div className="flex w-full flex-col gap-6">
      <h1 tabIndex={-1} className="sr-only">
        Hello, {name}. Clock in or out.
      </h1>

      {load === 'loading' && !status && <Loading>Loading your status…</Loading>}

      {load === 'error' && !status && (
        <Banner
          tone="error"
          title="We could not load your status."
          action={<Button variant="secondary" onClick={refresh}>Try again</Button>}
        />
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
            <Banner
              tone="error"
              title="You are offline or the server is down."
              action={<Button variant="secondary" onClick={refresh}>Check again</Button>}
            >
              What you see may be out of date. You can clock in or out once you are back online.
            </Banner>
          )}

          {refreshFailed && load !== 'offline' && (
            <Banner
              tone="warning"
              title="We could not refresh this page."
              action={<Button variant="secondary" onClick={refresh}>Try again</Button>}
            >
              What you see is the last answer we got from the server.
            </Banner>
          )}

          {problem && (
            <Banner
              tone={problem.tone ?? 'error'}
              title={problem.title}
              action={
                problem.retry && (
                  <Button variant="secondary" busy={busy} busyLabel="Trying again…" onClick={press}>
                    Try again
                  </Button>
                )
              }
            >
              {problem.text}
            </Banner>
          )}

          <div className="grid gap-4 md:min-h-[60vh] md:grid-cols-2 md:gap-6 [&>section]:min-h-56 md:[&>section]:min-h-0">
            {clockedIn ? [outBlock, inBlock] : [inBlock, outBlock]}
          </div>
        </>
      )}

      <p className="text-sm text-muted">All times are Zurich time and come from the server.</p>
    </div>
  )
}
