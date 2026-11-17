// DEV-ONLY mock of the backend, used by `npm run dev:mock` (vite --mode mock). It is a Vite
// dev-server plugin, so it never ends up in the production bundle.
//
// Pick a scenario with a query string on the PAGE url (the browser sends it as Referer):
//   http://localhost:5173/?mock=disagree#/time-lab
// Scenarios: accepted (default), single, lowprecision, dst, disagree, down, invalid,
//            notdev (404), servererror (500), unreachable (backend not running).
//
// Fixture times are fixed strings. The mock never reads a clock.

const TIME_NOW_RAW = JSON.stringify({
  abbreviation: 'CET',
  datetime: '2026-11-17T13:02:58.446011+01:00',
  day_of_week: 2,
  dst: false,
  timezone: 'Europe/Zurich',
  unixtime: 1794916978,
  utc_datetime: '2026-11-17T12:02:58.446011+00:00',
  utc_offset: '+01:00',
})

const TIMEAPI_RAW = JSON.stringify({
  year: 2026,
  month: 11,
  day: 17,
  hour: 13,
  minute: 2,
  seconds: 58,
  milliSeconds: 483,
  dateTime: '2026-11-17T13:02:58.4830000',
  timeZone: 'Europe/Zurich',
  dstActive: false,
})

const primaryOk = {
  role: 'primary',
  name: 'time.now',
  outcome: 'ok',
  roundTripMs: 341,
  httpStatus: null,
  detail: null,
  utc: '2026-11-17T12:02:58.446011Z',
  zurichOffset: '+01:00',
  zurichLocal: '2026-11-17T13:02:58.446011',
  rawPayload: TIME_NOW_RAW,
}

const secondaryOk = {
  role: 'secondary',
  name: 'timeapi.io',
  outcome: 'ok',
  roundTripMs: 402,
  httpStatus: null,
  detail: null,
  utc: '2026-11-17T12:02:58.483Z',
  zurichOffset: '+01:00',
  zurichLocal: '2026-11-17T13:02:58.483',
  rawPayload: TIMEAPI_RAW,
}

const failed = (base, outcome, detail, extra = {}) => ({
  ...base,
  outcome,
  detail,
  httpStatus: null,
  utc: null,
  zurichOffset: null,
  zurichLocal: null,
  rawPayload: null,
  ...extra,
})

const failedDecision = (code) => ({
  result: 'failed',
  provider: null,
  crossCheckedBy: null,
  lowPrecision: null,
  utc: null,
  zurichLocal: null,
  zurichOffset: null,
  code,
  httpStatus: 503,
})

const acceptedDecision = (p, crossCheckedBy, lowPrecision = false) => ({
  result: 'accepted',
  provider: p.name,
  crossCheckedBy,
  lowPrecision,
  utc: p.utc,
  zurichLocal: p.zurichLocal,
  zurichOffset: p.zurichOffset,
  code: null,
  httpStatus: null,
})

const lab = (providers, crossCheck, decision) => ({ status: 200, body: { providers, crossCheck, decision } })

const problem = (status, title, code, extraHeaders = {}) => ({
  status,
  headers: { 'Content-Type': 'application/problem+json', ...extraHeaders },
  body: { type: 'about:blank', title, status, code, traceId: '00-mock-00' },
})

const secondaryTimeout = failed(secondaryOk, 'timeout', 'no complete response within 3000 ms', { roundTripMs: 3001 })

const SCENARIOS = {
  accepted: () =>
    lab(
      [primaryOk, secondaryOk],
      { deltaMs: 37, toleranceMs: 2000, roundTripBoundMs: 371.5, agrees: true },
      acceptedDecision(primaryOk, 'timeapi.io'),
    ),

  single: () => {
    const primaryDown = failed(primaryOk, 'timeout', 'no complete response within 3000 ms', { roundTripMs: 3002 })
    return lab([primaryDown, secondaryOk], null, acceptedDecision(secondaryOk, null))
  },

  lowprecision: () => {
    const slow = { ...primaryOk, roundTripMs: 1834 }
    return lab(
      [slow, secondaryTimeout],
      null,
      acceptedDecision(slow, null, true),
    )
  },

  // T6: inside the repeated October hour; the second 02:30 is at +01:00 (CET).
  dst: () => {
    const p = {
      ...primaryOk,
      utc: '2026-10-25T01:30:12.104Z',
      zurichLocal: '2026-10-25T02:30:12.104',
      rawPayload: JSON.stringify({
        abbreviation: 'CET',
        datetime: '2026-10-25T02:30:12.104000+01:00',
        utc_datetime: '2026-10-25T01:30:12.104000+00:00',
        utc_offset: '+01:00',
        timezone: 'Europe/Zurich',
        dst: false,
      }),
    }
    const s = {
      ...secondaryOk,
      utc: '2026-10-25T01:30:12.150Z',
      zurichLocal: '2026-10-25T02:30:12.150',
      rawPayload: JSON.stringify({
        dateTime: '2026-10-25T02:30:12.1500000',
        timeZone: 'Europe/Zurich',
        dstActive: false,
      }),
    }
    return lab(
      [p, s],
      { deltaMs: 46, toleranceMs: 2000, roundTripBoundMs: 371.5, agrees: true },
      acceptedDecision(p, 'timeapi.io'),
    )
  },

  disagree: () => {
    const s = { ...secondaryOk, utc: '2026-11-17T12:03:03.483Z', zurichLocal: '2026-11-17T13:03:03.483' }
    return lab(
      [primaryOk, s],
      { deltaMs: 5037, toleranceMs: 2000, roundTripBoundMs: 371.5, agrees: false },
      failedDecision('time.providers_disagree'),
    )
  },

  down: () =>
    lab(
      [
        failed(primaryOk, 'rate_limited', 'provider answered 429 Too Many Requests', { httpStatus: 429, roundTripMs: 88 }),
        secondaryTimeout,
      ],
      null,
      failedDecision('time.unavailable'),
    ),

  invalid: () =>
    lab(
      [
        failed(primaryOk, 'invalid_response', 'body is not JSON', {
          roundTripMs: 120,
          rawPayload: '<html><body><h1>502 Bad Gateway</h1></body></html>',
        }),
        failed(secondaryOk, 'invalid_response', 'dstActive=true, but Europe/Zurich is not in DST on 2026-11-17', {
          roundTripMs: 290,
          rawPayload: TIMEAPI_RAW.replace('"dstActive":false', '"dstActive":true'),
        }),
      ],
      null,
      failedDecision('time.invalid_response'),
    ),

  notdev: () => problem(404, 'Not Found', 'not_found'),
  servererror: () => problem(500, 'An unexpected error occurred.', 'server.error'),
  unreachable: () => ({
    ...problem(502, 'API not reachable', 'dev.backend_unreachable'),
    headers: { 'Content-Type': 'application/problem+json', 'X-Dev-Proxy-Error': 'backend-unreachable' },
  }),
}

function scenarioFrom(req) {
  try {
    const name = new URL(req.headers.referer ?? '').searchParams.get('mock')
    return SCENARIOS[name] ? name : 'accepted'
  } catch {
    return 'accepted'
  }
}

export function mockApi({ delayMs = 800 } = {}) {
  return {
    name: 'clockmanager-mock-api',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const path = req.url?.split('?')[0]
        if (path === '/api/health') {
          res.writeHead(200, { 'Content-Type': 'application/json' })
          res.end(JSON.stringify({ status: 'ok (mock)' }))
          return
        }
        if (path !== '/api/dev/time-lab') return next()

        const name = scenarioFrom(req)
        const { status, headers, body } = SCENARIOS[name]()
        // A short delay so the busy state is visible, like the real round trips.
        setTimeout(() => {
          res.writeHead(status, { 'Content-Type': 'application/json', 'X-Mock-Scenario': name, ...headers })
          res.end(JSON.stringify(body))
        }, delayMs)
      })
      server.config.logger.info('  Mock API enabled: add ?mock=<scenario> to the page URL (see dev/mockApi.js)')
    },
  }
}
