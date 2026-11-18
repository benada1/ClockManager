// Punch screen logic without React or the DOM: a reducer for the state and a small async
// controller for the requests. The screen flips ONLY from a confirmed 201 (no optimistic update).
import { ApiError, NetworkError } from '../../api/client.js'
import describeFailure, { isExpectedShift, UNEXPECTED_ANSWER } from './describeFailure.js'

export const initialState = {
  status: null,
  syncAt: null, // monotonic time when `status` came from the server
  load: 'loading', // loading | ready | offline | error | inactive
  busy: false,
  problem: null,
  justClosedId: null,
  refreshFailed: false, // the last status refresh failed, the shown state is still the last confirmed one
}

export function reducer(state, action) {
  switch (action.type) {
    case 'status_ok':
      return { ...state, status: action.status, syncAt: action.syncAt, load: 'ready', refreshFailed: false }
    case 'status_failed':
      if (action.kind === 'inactive') return { ...state, load: 'inactive' }
      if (!state.status) return { ...state, load: action.kind }
      return { ...state, load: action.kind === 'offline' ? 'offline' : state.load, refreshFailed: true }
    case 'punch_started':
      return { ...state, busy: true, problem: null }
    case 'punch_confirmed': {
      // serverNow is cleared: the old one does not fit the new shift; the refresh brings a fresh one.
      const next = { ...state, busy: false, problem: null, status: { ...state.status, serverNow: null, flags: [] } }
      if (action.intent === 'clock-in') next.status.openShift = action.shift
      else Object.assign(next, { justClosedId: action.shift.shiftId, status: { ...next.status, openShift: null, lastClosedShift: action.shift } })
      return next
    }
    case 'punch_failed': {
      const next = { ...state, busy: false, problem: action.problem }
      if (action.openShift) next.status = { ...state.status, openShift: action.openShift, serverNow: null } // server's own answer to a 409
      return next
    }
    case 'punch_inactive':
      return { ...state, busy: false, load: 'inactive' }
    case 'punch_session_ended':
      return { ...state, busy: false } // the auth layer takes over
    default:
      return state
  }
}

/**
 * api: { getStatus(signal), punch(intent, key) }, newKey(): string, now(): monotonic ms.
 * Rules: one punch at a time; a punch is never aborted; only status fetches are aborted and
 * superseded (`seq` grows at every refresh start, punch start and confirmed punch).
 */
export function createPunchController({ api, newKey, now }) {
  let state = initialState
  const listeners = new Set()
  let seq = 0
  let statusAbort = null
  let inFlight = false
  let attempt = null // { intent, key }: kept only while the same attempt may be retried

  const dispatch = (action) => {
    state = reducer(state, action)
    listeners.forEach((l) => l())
  }

  async function refresh() {
    if (inFlight) return // paused: the punch settles first, then a refresh follows
    statusAbort?.abort()
    const abort = (statusAbort = new AbortController())
    const mine = ++seq
    try {
      const status = await api.getStatus(abort.signal)
      if (mine !== seq) return // older than a newer refresh or a punch
      dispatch({ type: 'status_ok', status, syncAt: now() })
    } catch (err) {
      if (mine !== seq || err?.name === 'AbortError') return
      if (err instanceof ApiError && err.status === 401) return // handled by the auth layer
      const kind = err instanceof ApiError && err.code === 'employee.inactive' ? 'inactive' : err instanceof NetworkError ? 'offline' : 'error'
      dispatch({ type: 'status_failed', kind })
    }
  }

  async function press() {
    if (inFlight || !state.status) return
    const intent = state.status.openShift ? 'clock-out' : 'clock-in'
    if (attempt?.intent !== intent) attempt = { intent, key: newKey() }
    inFlight = true
    seq++ // any status answer still on its way is now stale
    statusAbort?.abort()
    dispatch({ type: 'punch_started' })
    let skipRefresh = false
    try {
      const shift = await api.punch(intent, attempt.key) // never given a signal: it is never aborted
      if (!isExpectedShift(intent, shift)) {
        dispatch({ type: 'punch_failed', problem: UNEXPECTED_ANSWER }) // keep the key: a replay returns the stored result
      } else {
        attempt = null
        seq++
        dispatch({ type: 'punch_confirmed', intent, shift })
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        skipRefresh = true
        dispatch({ type: 'punch_session_ended' })
      } else if (err instanceof ApiError && err.code === 'employee.inactive') {
        skipRefresh = true
        dispatch({ type: 'punch_inactive' })
      } else {
        const problem = describeFailure(err)
        if (!problem.retry) attempt = null
        const openShift = err instanceof ApiError && err.code === 'shift.already_open' ? err.problem?.openShift : null
        dispatch({ type: 'punch_failed', problem, openShift })
      }
    } finally {
      inFlight = false
    }
    if (!skipRefresh) await refresh()
  }

  return {
    getState: () => state,
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    refresh,
    press,
    /** On unmount: only status fetches are cancelled, never a punch. */
    dispose: () => statusAbort?.abort(),
  }
}
