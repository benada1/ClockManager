import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, NetworkError } from '../../api/client.js'
import { getStatus, punch } from '../../api/punches.js'
import { generalMessage } from '../../lib/errors.js'

const TIME_DOWN = "We can't get a trusted time right now. Nothing was recorded."

/** Maps a failed punch to what the screen says and whether the same attempt can be retried. */
function describeFailure(err) {
  if (err instanceof NetworkError) {
    return {
      title: "We couldn't reach the server.",
      text: 'Your punch may or may not have been saved. Try again: it will not be recorded twice.',
      retry: true,
    }
  }
  if (!(err instanceof ApiError)) return { title: generalMessage(err), retry: true }
  switch (err.code) {
    case 'shift.already_open':
      return { tone: 'warning', title: 'You are already clocked in.', text: 'This can happen when you use another tab. We updated this page and recorded nothing new.' }
    case 'shift.not_open':
      return { tone: 'warning', title: 'You are not clocked in any more.', text: 'This can happen when you use another tab. We updated this page and recorded nothing new.' }
    case 'time.providers_disagree':
      return { title: TIME_DOWN, text: 'The time services do not agree with each other. Try again in a moment.', retry: true }
    case 'time.went_backwards':
      return { title: TIME_DOWN, text: 'The time we got is not later than your last punch. Try again in a moment.', retry: true }
    case 'time.unavailable':
    case 'time.invalid_response':
      return { title: TIME_DOWN, text: 'The time service is unavailable. Try again.', retry: true }
    case 'idempotency.key_reused':
      return { title: 'That attempt could not be sent again.', text: 'Nothing was recorded. Press the button once more.' }
    case 'request.invalid': {
      const messages = Object.values(err.errors ?? {}).flat()
      return { title: 'The server did not accept this punch.', text: messages.join(' ') || 'Nothing was recorded. Try again.' }
    }
    default:
      if (err.status === 429) return { title: 'Too many tries.', text: 'Wait a minute, then try again.', retry: true }
      return { title: generalMessage(err), text: 'Nothing was changed.', retry: true }
  }
}

/** State and actions of the punch screen. Times only ever come from server answers. */
export default function usePunch() {
  const [status, setStatus] = useState(null)
  const [load, setLoad] = useState('loading') // loading | ready | offline | error | inactive
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState(null)
  const [justClosedId, setJustClosedId] = useState(null)
  const attempt = useRef(null) // { intent, key }: kept only while the same attempt may be retried
  const busyRef = useRef(false)
  const seq = useRef(0)

  const refresh = useCallback(async () => {
    const mine = ++seq.current
    try {
      const data = await getStatus()
      if (mine !== seq.current) return
      setStatus(data)
      setLoad('ready')
    } catch (err) {
      if (mine !== seq.current) return
      if (err instanceof ApiError && err.code === 'employee.inactive') setLoad('inactive')
      else if (err instanceof NetworkError) setLoad('offline')
      else if (!(err instanceof ApiError && err.status === 401)) setLoad('error') // 401 is handled by the auth layer
    }
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  // Another tab may have punched: look again when this tab comes back or the network returns.
  useEffect(() => {
    const again = () => {
      if (document.visibilityState === 'visible' && !busyRef.current) refresh()
    }
    document.addEventListener('visibilitychange', again)
    window.addEventListener('focus', again)
    window.addEventListener('online', again)
    return () => {
      document.removeEventListener('visibilitychange', again)
      window.removeEventListener('focus', again)
      window.removeEventListener('online', again)
    }
  }, [refresh])

  const press = useCallback(async () => {
    if (busyRef.current || !status) return
    const intent = status.openShift ? 'clock-out' : 'clock-in'
    // A new key per attempt; the old one only while retrying the same attempt.
    if (attempt.current?.intent !== intent) attempt.current = { intent, key: crypto.randomUUID() }
    busyRef.current = true
    seq.current++ // answers of older status requests must not overwrite this result
    setBusy(true)
    setProblem(null)
    try {
      const shift = await punch(intent, attempt.current.key)
      attempt.current = null
      if (intent === 'clock-in') {
        setStatus((s) => ({ ...s, openShift: shift, flags: [] }))
      } else {
        setStatus((s) => ({ ...s, openShift: null, lastClosedShift: shift, flags: [] }))
        setJustClosedId(shift.shiftId)
      }
    } catch (err) {
      const failure = describeFailure(err)
      if (err instanceof ApiError && err.status === 401) return // session ended: the auth layer takes over
      if (err instanceof ApiError && err.code === 'employee.inactive') {
        setLoad('inactive')
      } else {
        if (!failure.retry) attempt.current = null
        setProblem(failure)
        if (err.code === 'shift.already_open' && err.problem?.openShift) {
          setStatus((s) => ({ ...s, openShift: err.problem.openShift }))
        }
      }
    } finally {
      busyRef.current = false
      setBusy(false)
    }
    refresh()
  }, [status, refresh])

  return { status, load, busy, problem, justClosedId, press, refresh }
}
