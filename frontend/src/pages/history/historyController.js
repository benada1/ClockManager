import { ApiError, NetworkError } from '../../api/client.js'
import { fieldErrors, generalMessage } from '../../lib/errors.js'
import { neighbourWeek } from './weeks.js'

/** What the page says when a load failed. The week on screen is kept. */
export function describeLoadFailure(err) {
  if (err instanceof NetworkError) return { kind: 'offline', title: 'You are offline or the server is down.', text: 'What you see may be out of date.' }
  if (err instanceof ApiError && err.status === 400) {
    const text = Object.values(fieldErrors(err)).join(' ')
    return { kind: 'invalid', title: 'We could not show that week.', text: text || 'Try another week.' }
  }
  return { kind: 'error', title: 'We could not load your shifts.', text: generalMessage(err) }
}

/**
 * Loads one week at a time. Every request gets a number; an answer for an older request is dropped,
 * so fast clicks can never show the wrong week. A failed load keeps the week on screen.
 */
export function createHistoryController({ api }) {
  let state = { data: null, loading: true, failure: null }
  let seq = 0
  let abort = null
  let target = null // the range of the last request (null = server's default week), used by retry
  const listeners = new Set()

  const set = (patch) => {
    state = { ...state, ...patch }
    listeners.forEach((l) => l())
  }

  function request(range) {
    const mine = ++seq
    abort?.abort()
    abort = new AbortController()
    target = range
    set({ loading: true, failure: null })
    return api
      .getShifts(range, abort.signal)
      .then((data) => {
        if (mine === seq) set({ data, loading: false })
      })
      .catch((err) => {
        if (mine !== seq || err?.name === 'AbortError') return
        set({ loading: false, failure: describeLoadFailure(err) })
      })
  }

  const step = (direction) => {
    const { data, loading } = state
    const range = data?.from && data?.to ? neighbourWeek(data, direction) : null
    if (loading || !range) return Promise.resolve()
    return request(range)
  }

  return {
    subscribe: (l) => (listeners.add(l), () => listeners.delete(l)),
    getState: () => state,
    open: () => request(null),
    previous: () => step(-1),
    next: () => step(1),
    retry: () => request(target),
    dispose() {
      seq++
      abort?.abort()
    },
  }
}
