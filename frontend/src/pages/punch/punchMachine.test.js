import { describe, expect, it } from 'vitest'
import { ApiError, NetworkError } from '../../api/client.js'
import { createPunchController } from './punchMachine.js'

const at = (t) => ({ utc: `2026-11-18T${t}:00.000Z`, zurich: `2026-11-18T${t}:00.000+01:00` })
const openShift = { shiftId: 's1', clockIn: at('07:00'), clockOut: null }
const closedShift = { shiftId: 's1', clockIn: at('07:00'), clockOut: at('16:00'), durationSeconds: 32400 }
const statusIn = { openShift, lastClosedShift: null, serverNow: at('08:00'), flags: [] }
const statusOut = { openShift: null, lastClosedShift: closedShift, serverNow: at('16:01'), flags: [] }
const statusIdle = { openShift: null, lastClosedShift: null, serverNow: at('06:00'), flags: [] }

const apiError = (status, code, extra = {}) => new ApiError({ status, code, ...extra })

function deferred() {
  const d = {}
  d.promise = new Promise((resolve, reject) => Object.assign(d, { resolve, reject }))
  return d
}

/** Fake API: each call is recorded; answers are scripted per test. */
function setup({ status = statusIn, punchResult, getStatus } = {}) {
  const calls = { status: [], punch: [] }
  let keys = 0
  let server = status // the fake server follows a successful punch, like the real one
  const c = createPunchController({
    api: {
      getStatus: (signal) => {
        calls.status.push(signal)
        return getStatus ? getStatus(signal, calls.status.length) : Promise.resolve(server)
      },
      punch: (...args) => {
        calls.punch.push(args)
        const answer = typeof punchResult === 'function' ? punchResult(...args) : Promise.resolve(punchResult)
        return answer.then((shift) => {
          if (shift?.clockOut === null) server = { ...server, openShift: shift }
          else if (shift?.clockOut) server = { ...server, openShift: null, lastClosedShift: shift }
          return shift
        })
      },
    },
    newKey: () => `key-${++keys}`,
    now: () => 0,
  })
  return { c, calls }
}

const loaded = async (opts) => {
  const t = setup(opts)
  await t.c.refresh()
  return t
}
const clockedIn = (c) => Boolean(c.getState().status.openShift)

describe('success flips only on a confirmed 201 with the expected body', () => {
  it('clock out: flips to clocked out and remembers the shift', async () => {
    const { c } = await loaded({ punchResult: closedShift })
    await c.press()
    expect(clockedIn(c)).toBe(false)
    expect(c.getState().status.lastClosedShift).toEqual(closedShift)
    expect(c.getState().justClosedId).toBe('s1')
  })

  it('clock in: flips to clocked in', async () => {
    const { c } = await loaded({ status: statusIdle, punchResult: openShift })
    await c.press()
    expect(clockedIn(c)).toBe(true)
  })

  it('shows the result right away, before the status refresh answers', async () => {
    const refresh = deferred()
    const t = setup({ punchResult: closedShift, getStatus: (_s, n) => (n === 1 ? Promise.resolve(statusIn) : refresh.promise) })
    await t.c.refresh()
    const pending = t.c.press()
    await Promise.resolve()
    await Promise.resolve()
    expect(clockedIn(t.c)).toBe(false) // confirmed result shown while refresh is pending
    refresh.resolve(statusOut)
    await pending
    expect(clockedIn(t.c)).toBe(false)
  })

  it('stays in flight (no flip) until the server answers', async () => {
    const answer = deferred()
    const { c } = await loaded({ punchResult: () => answer.promise })
    const pending = c.press()
    expect(c.getState().busy).toBe(true)
    expect(clockedIn(c)).toBe(true)
    answer.resolve(closedShift)
    await pending
    expect(c.getState().busy).toBe(false)
  })
})

describe('failures never flip the screen and show a message', () => {
  const cases = [
    ['503 time.unavailable', () => apiError(503, 'time.unavailable'), /trusted time/],
    ['429', () => apiError(429, 'request.rate_limited'), /Too many/],
    ['400', () => apiError(400, 'request.invalid', { errors: { intent: ['is wrong'] } }), /not accept/],
    ['422 key reused', () => apiError(422, 'idempotency.key_reused'), /could not be sent again/],
    ['403 inactive', () => apiError(403, 'employee.inactive'), null],
    ['500', () => apiError(500, 'server.error'), /our side/],
    ['network error', () => new NetworkError('down'), /reach the server/],
  ]
  for (const [name, make, message] of cases) {
    it(name, async () => {
      const { c } = await loaded({ punchResult: () => Promise.reject(make()) })
      await c.press()
      expect(clockedIn(c)).toBe(true)
      expect(c.getState().busy).toBe(false)
      if (message) expect(c.getState().problem.title + c.getState().problem.text).toMatch(message)
      else expect(c.getState().load).toBe('inactive')
    })
  }

  it('401 leaves the state alone for the auth layer', async () => {
    const { c } = await loaded({ punchResult: () => Promise.reject(apiError(401, 'auth.unauthenticated')) })
    await c.press()
    expect(clockedIn(c)).toBe(true)
    expect(c.getState().problem).toBeNull()
  })

  it('a 201 with the wrong body is an error and does not flip', async () => {
    for (const bad of [undefined, {}, openShift]) {
      const { c } = await loaded({ punchResult: bad })
      await c.press() // clock out answered with an open shift (or nothing)
      expect(clockedIn(c)).toBe(true)
      expect(c.getState().problem).toBeTruthy()
    }
    const { c } = await loaded({ status: statusIdle, punchResult: closedShift })
    await c.press() // clock in answered with a closed shift
    expect(clockedIn(c)).toBe(false)
    expect(c.getState().problem).toBeTruthy()
  })

  it('409 already open shows a warning and nothing is recorded', async () => {
    const { c } = await loaded({
      status: statusIdle,
      punchResult: () => Promise.reject(apiError(409, 'shift.already_open', { problem: { openShift } })),
    })
    await c.press()
    expect(c.getState().problem.tone).toBe('warning')
  })
})

describe('one request at a time, punches are never aborted', () => {
  it('a second click while in flight sends nothing', async () => {
    const answer = deferred()
    const { c, calls } = await loaded({ punchResult: () => answer.promise })
    const first = c.press()
    await c.press()
    await c.press()
    answer.resolve(closedShift)
    await first
    expect(calls.punch).toHaveLength(1)
  })

  it('a refresh or focus event during a punch does not touch it', async () => {
    const answer = deferred()
    const { c, calls } = await loaded({ punchResult: () => answer.promise })
    const pending = c.press()
    await c.refresh() // what focus/visibility do
    expect(calls.status).toHaveLength(1) // paused: no new status request
    answer.resolve(closedShift)
    await pending
    expect(clockedIn(c)).toBe(false)
  })

  it('dispose aborts status fetches only', async () => {
    const answer = deferred()
    const { c, calls } = await loaded({ punchResult: () => answer.promise })
    const pending = c.press()
    c.dispose()
    expect(calls.punch[0]).toHaveLength(2) // (intent, key): no signal given to the punch
    answer.resolve(closedShift)
    await pending
    expect(clockedIn(c)).toBe(false)
  })
})

describe('stale status answers are ignored', () => {
  it('an older status response does not overwrite a newer one', async () => {
    const slow = deferred()
    const { c } = setup({ getStatus: (_s, n) => (n === 1 ? slow.promise : Promise.resolve(statusOut)) })
    const first = c.refresh()
    await c.refresh() // newer
    slow.resolve(statusIn) // older, arrives late
    await first
    expect(clockedIn(c)).toBe(false)
  })

  it('a status started before the punch cannot undo the confirmed punch', async () => {
    const slow = deferred()
    const { c } = setup({
      punchResult: closedShift,
      getStatus: (_s, n) => (n === 1 ? Promise.resolve(statusIn) : n === 2 ? slow.promise : Promise.resolve(statusOut)),
    })
    await c.refresh()
    const stale = c.refresh() // slow request, still on its way
    await c.press()
    slow.resolve(statusIn) // arrives after the punch: shows clocked in
    await stale
    expect(clockedIn(c)).toBe(false)
  })

  it('a superseded status fetch gets aborted', async () => {
    const { c, calls } = setup({ getStatus: () => new Promise(() => {}) })
    c.refresh()
    c.refresh()
    expect(calls.status[0].aborted).toBe(true)
    expect(calls.status[1].aborted).toBe(false)
  })
})

describe('refresh after a punch', () => {
  it('keeps the confirmed result and shows a notice when the refresh fails', async () => {
    const { c } = setup({
      punchResult: closedShift,
      getStatus: (_s, n) => (n === 1 ? Promise.resolve(statusIn) : Promise.reject(apiError(500, 'server.error'))),
    })
    await c.refresh()
    await c.press()
    expect(clockedIn(c)).toBe(false)
    expect(c.getState().refreshFailed).toBe(true)
    await c.refresh() // retry still failing: still not reverted
    expect(clockedIn(c)).toBe(false)
  })

  it('a successful refresh clears the notice', async () => {
    let fail = true
    const { c } = setup({
      punchResult: closedShift,
      getStatus: (_s, n) => (n === 1 || !fail ? Promise.resolve(fail ? statusIn : statusOut) : Promise.reject(apiError(500))),
    })
    await c.refresh()
    await c.press()
    fail = false
    await c.refresh()
    expect(c.getState().refreshFailed).toBe(false)
  })
})

describe('idempotency key', () => {
  it('is reused when retrying after a 503', async () => {
    let n = 0
    const { c, calls } = await loaded({
      punchResult: () => (++n === 1 ? Promise.reject(apiError(503, 'time.unavailable')) : Promise.resolve(closedShift)),
    })
    await c.press()
    await c.press()
    expect(calls.punch[0][1]).toBe(calls.punch[1][1])
    expect(clockedIn(c)).toBe(false)
  })

  it('is new after a 409', async () => {
    let n = 0
    const { c, calls } = await loaded({
      punchResult: () => (++n === 1 ? Promise.reject(apiError(409, 'shift.not_open')) : Promise.resolve(closedShift)),
    })
    await c.press()
    await c.press()
    expect(calls.punch[0][1]).not.toBe(calls.punch[1][1])
  })

})
