import { useEffect, useRef, useState } from 'react'
import { ApiError, NetworkError } from '../api/client.js'
import { getTimeLab } from '../api/timeLab.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import CrossCheckPanel from './time-lab/CrossCheckPanel.jsx'
import DecisionPanel from './time-lab/DecisionPanel.jsx'
import ProviderCard from './time-lab/ProviderCard.jsx'

function describeError(err) {
  if (err instanceof NetworkError) {
    return {
      title: 'The API is not running',
      body: 'The browser could not reach the backend. Start it (cd backend/ClockManager.Api; dotnet run) and try again.',
    }
  }
  if (err instanceof ApiError && err.status === 404) {
    return {
      title: 'Time lab is not available',
      body: 'The backend answered 404: this endpoint only exists when the API runs in the Development environment.',
    }
  }
  if (err instanceof ApiError) {
    return {
      title: `Unexpected error (HTTP ${err.status})`,
      body: [err.code, err.detail || err.title].filter(Boolean).join(': ') || 'The server returned an error.',
    }
  }
  return { title: 'Unexpected error', body: err?.message || 'Something went wrong while reading the answer.' }
}

function summarize(data, requestNo) {
  const d = data.decision
  if (d?.result === 'accepted') {
    const by = d.crossCheckedBy ? `cross-checked by ${d.crossCheckedBy}` : 'single source, not cross-checked'
    return `Answer ${requestNo}: accepted from ${d.provider}, ${by}.`
  }
  return `Answer ${requestNo}: failed closed with ${d?.code ?? 'an unknown error'}.`
}

function TimeLab() {
  // status: idle | loading | success | error
  const [state, setState] = useState({ status: 'idle', data: null, error: null })
  const [requestNo, setRequestNo] = useState(0)
  const inFlight = useRef(false)
  const controllerRef = useRef(null)

  useEffect(() => () => controllerRef.current?.abort(), [])

  const busy = state.status === 'loading'

  async function ask() {
    if (inFlight.current) return // ignore double clicks while a request is running
    inFlight.current = true
    const controller = new AbortController()
    controllerRef.current = controller
    const no = requestNo + 1
    setRequestNo(no)
    setState((s) => ({ ...s, status: 'loading', error: null }))
    try {
      const data = await getTimeLab({ signal: controller.signal })
      if (!data || !Array.isArray(data.providers) || !data.decision) {
        throw new Error('The server answered, but not in the expected time-lab format.')
      }
      setState({ status: 'success', data, error: null })
    } catch (err) {
      if (err?.name === 'AbortError') return
      // Drop the previous result: showing stale provider answers next to an error would mislead.
      setState({ status: 'error', data: null, error: err })
    } finally {
      inFlight.current = false
    }
  }

  const { data, error } = state
  const problem = error ? describeError(error) : null

  let statusText = ''
  if (busy) statusText = 'Asking the providers...'
  else if (state.status === 'success') statusText = summarize(data, requestNo)
  else if (problem) statusText = `${problem.title}.`

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6">
      <div className="flex flex-col gap-2">
        <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight">
          Time lab
        </h1>
        <p className="max-w-3xl text-base text-ink-2">
          Every punch time comes from external time providers, fetched and checked by our server. The browser clock is never
          used. Ask the providers once to see what each answered, whether it passed validation, and what time a punch would get.
        </p>
        <p className="text-sm text-muted">Development-only diagnostic. Each click makes one fresh call; nothing is cached or saved.</p>
      </div>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-3">
        <Button size="lg" onClick={ask} busy={busy} busyLabel="Asking providers..." aria-describedby="time-lab-status">
          Ask both providers
        </Button>
        {requestNo > 0 && <span className="text-sm text-muted">Requests made on this page: {requestNo}</span>}
      </div>

      <section aria-labelledby="results-heading" aria-busy={busy} className="flex flex-col gap-6">
        <h2 id="results-heading" className="sr-only">
          Results
        </h2>
        <p id="time-lab-status" className="min-h-6 text-base font-semibold" aria-live="polite" aria-atomic="true">
          {statusText}
        </p>

        {problem && (
          <Banner tone="error" title={problem.title} announce={false}>
            {problem.body}
          </Banner>
        )}

        {data && (
          <div className={`flex flex-col gap-6 transition-opacity ${busy ? 'opacity-55' : ''}`}>
            <DecisionPanel decision={data.decision} />
            <h2 className="text-lg font-semibold">Providers</h2>
            <div className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,20rem),1fr))] gap-6">
              {data.providers.map((p) => (
                <ProviderCard key={`${p.role}-${p.name}`} provider={p} />
              ))}
            </div>
            <CrossCheckPanel crossCheck={data.crossCheck} />
          </div>
        )}

        {state.status === 'idle' && <p className="text-base text-muted">No answer yet. Press the button to ask the providers.</p>}
      </section>
    </div>
  )
}

export default TimeLab
