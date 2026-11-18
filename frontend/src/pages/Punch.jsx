import { useAuth } from '../auth/authContext.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card from '../components/Card.jsx'
import Loading from '../components/Loading.jsx'
import ShiftSummary, { ShiftNotices } from './punch/ShiftSummary.jsx'
import usePunch from './punch/usePunch.js'

export default function Punch() {
  const { user } = useAuth()
  const { status, load, busy, problem, justClosedId, press, refresh } = usePunch()
  const clockedIn = Boolean(status?.openShift)
  const name = status?.employee?.displayName ?? user?.displayName

  return (
    <div className="mx-auto flex w-full max-w-md flex-col gap-6">
      <h1 tabIndex={-1} className="text-xl font-semibold text-ink">
        Hello, {name}
      </h1>

      <Card className="flex flex-col gap-5">
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
            <div aria-live="polite" className="flex flex-col gap-1">
              <p className="text-xl font-semibold text-ink">
                {clockedIn ? 'You are clocked in' : 'You are not clocked in'}
              </p>
              <ShiftSummary status={status} justClosedId={justClosedId} />
            </div>

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

            {!problem?.retry && (
              <Button
                size="lg"
                block
                busy={busy}
                busyLabel={clockedIn ? 'Clocking out…' : 'Clocking in…'}
                disabled={load === 'offline'}
                onClick={press}
              >
                {clockedIn ? 'Clock out' : 'Clock in'}
              </Button>
            )}
          </>
        )}
      </Card>

      <p className="text-sm text-muted">All times are Zurich time and come from the server.</p>
    </div>
  )
}
