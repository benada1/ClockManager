// DEV-ONLY mock of the auth, status and punch endpoints (plan 04 section 3 + Phase 3), used by
// `npm run dev:mock`. Pick a scenario with ?mock=<name> on the PAGE url, e.g. http://localhost:5173/?mock=open
//
// Default (no ?mock): signed out. Sign up or log in with anything (username "taken" is taken), then punch.
// Signed-in states (no login needed):  open, closed, fresh, forgotten, longshift, dstday,
//                                       notime (open shift but no trusted server time: no counter)
// Punch outcomes (signed in, closed):   conflict (409 on clock in), conflictout (open, 409 on clock out),
//                                       unavailable (503), disagree (503), flaky (503 once, then ok),
//                                       badrequest (400), keyreused (422), toomany (429), servererror (500),
//                                       expired (401 on punch), offlinepunch (connection drops on punch)
// Other:  badlogin (login 401), blocked (login/signup 429), inactive (403), offline (nothing answers)
//
// Fixture times are fixed strings. The mock never reads a clock.

const IN = { utc: '2026-11-18T07:03:11.000Z', zurich: '2026-11-18T08:03:11.000+01:00' }
const OUT = { utc: '2026-11-18T16:12:40.000Z', zurich: '2026-11-18T17:12:40.000+01:00' }
const YESTERDAY_IN = { utc: '2026-11-17T07:03:11.000Z', zurich: '2026-11-17T08:03:11.000+01:00' }

// Fixed "server now" for the elapsed counter (no clock reads): 2 h 38 min after IN.
const SERVER_NOW = { utc: '2026-11-18T09:41:07.000Z', zurich: '2026-11-18T10:41:07.000+01:00', ageMs: 120 }

const punchView = (t) => ({ ...t, provider: 'time.now', crossCheckedBy: 'timeapi.io', lowPrecision: false })
const shift = (id, tin, tout, seconds, flags = []) => ({
  shiftId: id,
  workDate: tin.zurich.slice(0, 10),
  clockIn: punchView(tin),
  clockOut: tout ? punchView(tout) : null,
  durationSeconds: tout ? seconds : null,
  flags,
})

const CLOSED = () => shift('mock-last', { utc: '2026-11-17T07:00:00.000Z', zurich: '2026-11-17T08:00:00.000+01:00' }, { utc: '2026-11-17T16:30:00.000Z', zurich: '2026-11-17T17:30:00.000+01:00' }, 34200)

// Initial shift state per scenario: [open, lastClosed, status flags]
const START = {
  open: () => [shift('mock-open', IN, null), null, []],
  closed: () => [null, CLOSED(), []],
  fresh: () => [null, null, []],
  forgotten: () => [shift('mock-old', YESTERDAY_IN, null, null, ['long_shift']), null, ['forgotten_clock_out']],
  longshift: () => [
    null,
    shift('mock-long', { utc: '2026-11-17T06:00:00.000Z', zurich: '2026-11-17T07:00:00.000+01:00' }, { utc: '2026-11-17T23:00:00.000Z', zurich: '2026-11-18T00:00:00.000+01:00' }, 61200, ['long_shift']),
    [],
  ],
  // T6: inside the repeated October hour, +02:00 then +01:00 (45 min)
  dstday: () => [
    null,
    shift('mock-dst', { utc: '2026-10-25T00:30:00.000Z', zurich: '2026-10-25T02:30:00.000+02:00' }, { utc: '2026-10-25T01:15:00.000Z', zurich: '2026-10-25T02:15:00.000+01:00' }, 2700),
    [],
  ],
  conflictout: () => [shift('mock-open', IN, null), null, []],
  notime: () => [shift('mock-open', IN, null), null, []],
}
const SIGNED_IN = new Set([
  ...Object.keys(START),
  'conflict', 'unavailable', 'disagree', 'flaky', 'badrequest', 'keyreused', 'toomany', 'servererror',
  'expired', 'offlinepunch', 'inactive',
])

const state = { scenario: null, open: null, last: null, flags: [], seen: new Map(), flakyUsed: false, user: null }

function reset(name) {
  if (state.scenario === name) return
  const [open, last, flags] = (START[name] ?? START.fresh)()
  Object.assign(state, { scenario: name, open, last, flags, seen: new Map(), flakyUsed: false })
}

const problem = (status, title, code, extra = {}) => ({
  status,
  body: { type: 'about:blank', title, status, code, traceId: '00-mock-00', ...extra },
})

const cookies = (req) => Object.fromEntries((req.headers.cookie ?? '').split(/;\s*/).filter(Boolean).map((c) => c.split('=')))

function scenarioOf(req) {
  try {
    return new URL(req.headers.referer ?? '').searchParams.get('mock') ?? ''
  } catch {
    return ''
  }
}

function readJson(req) {
  return new Promise((resolve) => {
    let raw = ''
    req.on('data', (c) => (raw += c))
    req.on('end', () => {
      try {
        resolve(raw ? JSON.parse(raw) : {})
      } catch {
        resolve(null)
      }
    })
  })
}

function validateSignup(b) {
  const errors = {}
  if (!/^[A-Za-z0-9._-]{3,32}$/.test(b.username ?? '')) errors.username = ['must be 3-32 characters: letters, digits, ".", "_" or "-"']
  if (!b.displayName || b.displayName.length > 128) errors.displayName = ['is required (up to 128 characters)']
  if (typeof b.password !== 'string' || b.password.length < 8 || b.password.length > 128) errors.password = ['must be 8-128 characters']
  return errors
}

const userOf = () => state.user ?? { username: 'alice', displayName: 'Alice Example', role: 'Employee' }

/** Returns { status, body, headers? } or 'drop' (cut the connection), or null when not an auth/punch route. */
async function route(req, scenario, authed) {
  const path = req.url.split('?')[0]
  const unauth = problem(401, 'Not logged in.', 'auth.unauthenticated')

  if (path === '/api/auth/signup' || path === '/api/auth/login') {
    if (scenario === 'blocked') return problem(429, 'Too many requests.', 'request.rate_limited')
    const body = await readJson(req)
    if (!body) return problem(400, 'Invalid request.', 'request.invalid', { errors: { body: ['must be a JSON object'] } })
    if (path.endsWith('signup')) {
      const errors = validateSignup(body)
      if (Object.keys(errors).length) return problem(400, 'One or more validation errors occurred.', 'request.invalid', { errors })
      if (body.username.toLowerCase() === 'taken') return problem(409, 'Username is taken.', 'auth.username_taken')
      state.user = { username: body.username, displayName: body.displayName, role: 'Employee' }
      reset('fresh')
      return { status: 201, body: state.user, headers: { 'Set-Cookie': 'mock_session=1; Path=/; HttpOnly' } }
    }
    if (scenario === 'badlogin' || !body.username || !body.password) {
      return problem(401, 'Wrong username or password.', 'auth.invalid_credentials')
    }
    state.user = { username: body.username, displayName: body.username, role: 'Employee' }
    return { status: 200, body: state.user, headers: { 'Set-Cookie': 'mock_session=1; Path=/; HttpOnly' } }
  }

  if (path === '/api/auth/logout') {
    return { status: 204, headers: { 'Set-Cookie': 'mock_session=0; Path=/; HttpOnly' } }
  }
  if (path === '/api/auth/me') return authed ? { status: 200, body: userOf() } : unauth

  if (path === '/api/me/status') {
    if (!authed) return unauth
    if (scenario === 'inactive') return problem(403, 'Account is switched off.', 'employee.inactive')
    return {
      status: 200,
      body: { employee: userOf(), openShift: state.open, lastClosedShift: state.last, serverNow: scenario === 'notime' ? null : SERVER_NOW, flags: state.flags },
    }
  }

  if (path === '/api/punches' && req.method === 'POST') {
    if (!authed || scenario === 'expired') return unauth
    if (scenario === 'inactive') return problem(403, 'Account is switched off.', 'employee.inactive')
    if (scenario === 'offlinepunch') return 'drop'
    const key = req.headers['idempotency-key']
    const body = await readJson(req)
    if (!key) return problem(400, 'Idempotency-Key is required.', 'idempotency.key_missing')
    const intent = body?.intent
    if (scenario === 'badrequest' || (intent !== 'clock-in' && intent !== 'clock-out')) {
      return problem(400, 'One or more validation errors occurred.', 'request.invalid', {
        errors: { intent: ['must be "clock-in" or "clock-out"'] },
      })
    }
    if (scenario === 'keyreused') return problem(422, 'Key reused.', 'idempotency.key_reused')
    if (scenario === 'toomany') return problem(429, 'Too many requests.', 'request.rate_limited')
    if (scenario === 'servererror') return problem(500, 'An unexpected error occurred.', 'server.error')
    if (state.seen.has(key)) return { status: 201, body: state.seen.get(key) } // replay of a stored success
    if (scenario === 'unavailable') return problem(503, 'No trusted time.', 'time.unavailable')
    if (scenario === 'disagree') return problem(503, 'Providers disagree.', 'time.providers_disagree')
    if (scenario === 'flaky' && !state.flakyUsed) {
      state.flakyUsed = true
      return problem(503, 'No trusted time.', 'time.unavailable')
    }
    // `conflict` starts closed but "another tab" already clocked in; after the 409 the state matches.
    if (scenario === 'conflict' && intent === 'clock-in' && !state.open) {
      state.open = shift('mock-other-tab', IN, null)
      return problem(409, 'Already open.', 'shift.already_open', { openShift: state.open })
    }
    if (intent === 'clock-in') {
      if (state.open) return problem(409, 'Already open.', 'shift.already_open', { openShift: state.open })
      state.open = shift('mock-new', IN, null)
      state.flags = []
      state.seen.set(key, state.open)
      return { status: 201, body: state.open }
    }
    if (!state.open) return problem(409, 'Not open.', 'shift.not_open')
    if (scenario === 'conflictout' && state.open.shiftId === 'mock-open') {
      state.last = shift('mock-other-tab', IN, OUT, 32969)
      state.open = null
      return problem(409, 'Not open.', 'shift.not_open')
    }
    const closed = shift(state.open.shiftId, { utc: state.open.clockIn.utc, zurich: state.open.clockIn.zurich }, OUT, Math.round((Date.parse(OUT.utc) - Date.parse(state.open.clockIn.utc)) / 1000))
    state.open = null
    state.last = closed
    state.flags = []
    state.seen.set(key, closed)
    return { status: 201, body: closed }
  }
  return null
}

/** Connect-style handler: returns true when it answered the request. */
export async function handleAuthMock(req, res, { delayMs = 400 } = {}) {
  const path = req.url?.split('?')[0] ?? ''
  if (!/^\/api\/(auth|punches|me\/status)/.test(path)) return false

  const scenario = scenarioOf(req)
  reset(START[scenario] || SIGNED_IN.has(scenario) ? scenario : 'fresh')
  if (scenario === 'offline') {
    req.socket.destroy()
    return true
  }
  const session = cookies(req).mock_session
  const authed = session === '1' || (session !== '0' && SIGNED_IN.has(scenario))

  const out = await route(req, scenario, authed)
  if (out === null) return false
  if (out === 'drop') {
    setTimeout(() => req.socket.destroy(), delayMs)
    return true
  }
  setTimeout(() => {
    res.writeHead(out.status, {
      'Content-Type': out.status >= 400 ? 'application/problem+json' : 'application/json',
      'X-Mock-Scenario': scenario || 'default',
      ...out.headers,
    })
    res.end(out.body === undefined ? undefined : JSON.stringify(out.body))
  }, delayMs)
  return true
}
