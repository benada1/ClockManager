// DEV-ONLY mock of the manager endpoints (plan 07 section 5), used through dev/mockAuth.js.
// Scenarios (?mock=...): manager (stateful corrections), managerstale (first save: 409 stale, the shift
// was changed by "someone else"), managerfuture (422 in_future), manageroverlap (409 overlap),
// managerrunning (409 shift_still_running), managerdown (503 on save), notmanager (signed in as an Employee: 403).
// Fixed fixture strings only. Date.parse reads the given strings, never the clock.

const NOW_UTC = '2026-11-18T09:41:07Z' // the fixed "trusted now" of the mock
const AT = '2026-11-18T10:41:07+01:00'
const MARIA = { id: 4, displayName: 'Maria Manager' }
const MAX_OPEN_S = 16 * 3600

export const MANAGER_SCENARIOS = new Set(['manager', 'managerstale', 'managerfuture', 'manageroverlap', 'managerrunning', 'managerdown'])

const EMPLOYEES = [
  { id: 1, username: 'alice', displayName: 'Alice Example', role: 'Employee', isActive: true },
  { id: 2, username: 'bob', displayName: 'Bob Builder', role: 'Employee', isActive: true },
  { id: 3, username: 'carla', displayName: 'Carla Former', role: 'Employee', isActive: false },
  { id: 4, username: 'manager', displayName: 'Maria Manager', role: 'Manager', isActive: true },
]

const sh = (shiftId, employeeId, inZ, outZ, flags = []) => ({ shiftId, employeeId, inZ, outZ, flags, version: 1 })

function freshShifts() {
  return [
    sh(101, 1, '2026-11-16T08:00:00+01:00', '2026-11-16T17:00:00+01:00'),
    sh(102, 1, '2026-11-17T08:03:11+01:00', '2026-11-17T17:12:20+01:00'),
    sh(103, 1, '2026-11-18T08:03:11+01:00', null), // clocked in now: may still be running
    sh(201, 2, '2026-11-13T09:00:00+01:00', '2026-11-13T09:02:00+01:00', ['short_shift']),
    sh(202, 2, '2026-11-16T07:00:00+01:00', '2026-11-16T23:30:00+01:00', ['long_shift']),
    sh(203, 2, '2026-11-17T08:03:11+01:00', null, ['forgotten_clock_out']), // forgotten
    sh(301, 3, '2026-10-24T22:00:00+02:00', '2026-10-25T06:00:00+01:00'), // across DST end: 9 h
    sh(302, 3, '2026-10-25T02:30:00+02:00', '2026-10-25T02:15:00+01:00'), // T6: repeated hour
  ]
}

function freshCorrections() {
  return [
    { id: 1, shiftId: 102, field: 'clockIn', old: { zurich: '2026-11-17T08:30:00+01:00' }, new: { zurich: '2026-11-17T08:03:11+01:00' }, reason: 'Badge reader was down in the morning; she started at 08:03.', by: MARIA, at: { zurich: '2026-11-17T18:02:00+01:00' } },
    { id: 2, shiftId: 102, field: 'clockOut', old: { zurich: '2026-11-17T16:00:00+01:00' }, new: { zurich: '2026-11-17T17:12:20+01:00' }, reason: 'Stayed for the late delivery, confirmed by the team lead.', by: MARIA, at: { zurich: '2026-11-18T09:40:00+01:00' } },
  ]
}

export const createManagerState = () => ({ shifts: freshShifts(), corrections: freshCorrections(), staleUsed: false })

const seconds = (a, b) => Math.round((Date.parse(b) - Date.parse(a)) / 1000)

function view(s, state) {
  return {
    shiftId: s.shiftId,
    workDate: s.inZ.slice(0, 10),
    clockIn: { zurich: s.inZ },
    clockOut: s.outZ ? { zurich: s.outZ } : null,
    durationSeconds: s.outZ ? seconds(s.inZ, s.outZ) : null,
    endsNextDay: Boolean(s.outZ) && s.outZ.slice(0, 10) > s.inZ.slice(0, 10),
    open: !s.outZ,
    flags: s.flags,
    corrected: state.corrections.some((c) => c.shiftId === s.shiftId),
    rowVersion: `AAAAAAAA${String(s.shiftId).padStart(4, '0')}${s.version}=`,
  }
}

function detail(s, state) {
  const e = EMPLOYEES.find((x) => x.id === s.employeeId)
  return {
    shift: view(s, state),
    employee: { id: e.id, displayName: e.displayName },
    corrections: state.corrections.filter((c) => c.shiftId === s.shiftId),
  }
}

function flagsAfter(s) {
  if (!s.outZ) return s.flags
  const d = seconds(s.inZ, s.outZ)
  return d >= 12 * 3600 ? ['long_shift'] : d < 5 * 60 ? ['short_shift'] : []
}

const KIND_ORDER = ['forgotten_clock_out', 'long_shift', 'short_shift']

function employees(state) {
  return EMPLOYEES.map((e) => {
    const open = state.shifts.find((s) => s.employeeId === e.id && !s.outZ)
    return { ...e, status: open ? 'clocked_in' : 'clocked_out', since: open ? { zurich: open.inZ } : null, flags: open?.flags.includes('forgotten_clock_out') ? ['forgotten_clock_out'] : [] }
  })
}

function anomalies(state) {
  return state.shifts
    .flatMap((s) => s.flags.filter((f) => KIND_ORDER.includes(f)).map((kind) => ({ s, kind })))
    .sort((a, b) => (a.s.inZ < b.s.inZ ? 1 : -1))
    .map(({ s, kind }) => ({
      shiftId: s.shiftId,
      employeeId: s.employeeId,
      displayName: EMPLOYEES.find((e) => e.id === s.employeeId).displayName,
      workDate: s.inZ.slice(0, 10),
      kind,
      clockIn: { zurich: s.inZ },
      clockOut: s.outZ ? { zurich: s.outZ } : null,
      durationSeconds: s.outZ ? seconds(s.inZ, s.outZ) : null,
    }))
}

const ISO_WITH_OFFSET = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?[+-]\d{2}:\d{2}$/

function validate(b) {
  const errors = {}
  if (b?.field !== 'clockIn' && b?.field !== 'clockOut') errors.field = ['must be "clockIn" or "clockOut"']
  if (typeof b?.newTime !== 'string' || !ISO_WITH_OFFSET.test(b.newTime) || Number.isNaN(Date.parse(b.newTime))) {
    errors.newTime = ['must be a Zurich time with its offset, like 2026-11-17T17:12:00+01:00']
  }
  const reason = typeof b?.reason === 'string' ? b.reason.trim() : ''
  if (reason.length < 5 || reason.length > 500) errors.reason = ['must be 5 to 500 characters']
  return errors
}

/** Returns { status, body } for /api/manager/*, or null when the path is not a manager route. */
export async function managerRoute(req, { scenario, authed, state, readJson, problem, shiftsAnswer }) {
  const path = req.url.split('?')[0]
  if (!path.startsWith('/api/manager/')) return null
  if (!authed) return problem(401, 'Not logged in.', 'auth.unauthenticated')
  if (!MANAGER_SCENARIOS.has(scenario) && !state.isManagerLogin) return problem(403, 'Managers only.', 'auth.forbidden')

  if (path === '/api/manager/employees') return { status: 200, body: employees(state) }
  if (path === '/api/manager/anomalies') return { status: 200, body: anomalies(state) }

  let m = /^\/api\/manager\/employees\/(\d+)\/shifts$/.exec(path)
  if (m) {
    const id = Number(m[1])
    if (!EMPLOYEES.some((e) => e.id === id)) return problem(404, 'Not found.', 'request.not_found')
    return shiftsAnswer(req.url, state.shifts.filter((s) => s.employeeId === id).map((s) => view(s, state)))
  }

  m = /^\/api\/manager\/shifts\/(\d+)(\/corrections)?$/.exec(path)
  const s = m && state.shifts.find((x) => x.shiftId === Number(m[1]))
  if (m && !s) return problem(404, 'Not found.', 'request.not_found')
  if (m && !m[2]) return { status: 200, body: detail(s, state) }
  if (!m || req.method !== 'POST') return problem(404, 'Not found.', 'request.not_found')

  const body = await readJson(req)
  const errors = validate(body)
  if (Object.keys(errors).length) return problem(400, 'One or more validation errors occurred.', 'request.invalid', { errors })
  if (scenario === 'managerstale' && !state.staleUsed) {
    state.staleUsed = true
    s.version += 1 // someone else saved this shift meanwhile
  }
  if (body.rowVersion !== view(s, state).rowVersion) return problem(409, 'The shift was changed meanwhile.', 'correction.stale')
  if (scenario === 'managerdown') return problem(503, 'No trusted time.', 'time.unavailable')
  if (scenario === 'managerfuture' || Date.parse(body.newTime) > Date.parse(NOW_UTC)) return problem(422, 'In the future.', 'correction.in_future')
  if (scenario === 'manageroverlap') return problem(409, 'Overlaps another shift.', 'correction.overlap')

  const nextIn = body.field === 'clockIn' ? body.newTime : s.inZ
  const nextOut = body.field === 'clockOut' ? body.newTime : s.outZ
  const closing = !s.outZ && body.field === 'clockOut'
  if (scenario === 'managerrunning' || (closing && seconds(s.inZ, NOW_UTC) < MAX_OPEN_S)) {
    return problem(409, 'The shift may still be running.', 'correction.shift_still_running')
  }
  if (nextOut && Date.parse(nextOut) <= Date.parse(nextIn)) return problem(422, 'Clock out must be after clock in.', 'correction.invalid_order')
  const overlaps = state.shifts.some(
    (o) => o !== s && o.employeeId === s.employeeId && Date.parse(o.inZ) < Date.parse(nextOut ?? NOW_UTC) && Date.parse(o.outZ ?? NOW_UTC) > Date.parse(nextIn),
  )
  if (overlaps) return problem(409, 'Overlaps another shift.', 'correction.overlap')

  const oldIso = body.field === 'clockIn' ? s.inZ : s.outZ
  state.corrections.push({
    id: state.corrections.length + 1,
    shiftId: s.shiftId,
    field: body.field,
    old: oldIso ? { zurich: oldIso } : null,
    new: { zurich: body.newTime },
    reason: body.reason.trim(),
    by: MARIA,
    at: { zurich: AT },
  })
  s.inZ = nextIn
  s.outZ = nextOut
  s.flags = flagsAfter(s)
  s.version += 1
  return { status: 201, body: detail(s, state) }
}
