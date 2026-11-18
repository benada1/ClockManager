// QA (phase 8): the correction form against REAL punch values. Real punches carry seconds (08:03:11), the form works in minutes.
import { describe, expect, it } from 'vitest'
import { toZurichInput } from '../../lib/zurichInput.js'
import { buildCorrection, describeCorrectionFailure } from './correction.js'
import { ApiError } from '../../api/client.js'

const real = {
  shiftId: 102,
  workDate: '2026-11-17',
  clockIn: { zurich: '2026-11-17T08:03:11+01:00' },
  clockOut: { zurich: '2026-11-17T17:12:20.456+01:00' },
  open: false,
  rowVersion: 'AAAAAAAAB9E=',
}

describe('QA-28: opening the dialog and saving without touching the time', () => {
  // The inputs are prefilled from the current value but cut to the minute. If the manager changes only the reason
  // (or picks the other field by mistake and saves), the "already the current time" check never fires and the server
  // records a change of a few seconds with the audit row "17:12 to 17:12".
  it('QA-28: an untouched prefilled time is refused as unchanged', () => {
    const form = { field: 'clockOut', ...toZurichInput(real.clockOut.zurich), reason: 'Checking the record' }
    const built = buildCorrection(form, real)
    expect(built.ok).toBe(false)
    expect(built.errors.newTime).toMatch(/already/)
  })

  it('pin: the prefill is cut to the minute (seconds are not shown or kept)', () => {
    expect(toZurichInput(real.clockOut.zurich)).toEqual({ date: '2026-11-17', time: '17:12', offset: '+01:00' })
  })

  it('pin: a real change of at least a minute is built with seconds 00 and the Zurich offset', () => {
    const form = { field: 'clockOut', date: '2026-11-17', time: '18:05', offset: null, reason: 'Stayed for the delivery' }
    expect(buildCorrection(form, real)).toMatchObject({ ok: true, body: { newTime: '2026-11-17T18:05:00+01:00' } })
  })
})

describe('error codes the correction dialog can get', () => {
  const e = (status, code, errors = null) => new ApiError({ status, code, errors })
  it('maps every correction.* code to words next to the time field', () => {
    for (const code of ['correction.overlap', 'correction.shift_still_running', 'correction.in_future', 'correction.invalid_order', 'correction.unchanged']) {
      expect(describeCorrectionFailure(e(code === 'correction.overlap' || code === 'correction.shift_still_running' ? 409 : 422, code))).toMatchObject({ place: 'fields' })
    }
    expect(describeCorrectionFailure(e(409, 'correction.stale')).place).toBe('stale')
  })

  it('a bad offset from the server (400 newTime) is shown at the time field', () => {
    const f = describeCorrectionFailure(e(400, 'request.invalid', { newTime: ['the offset is not the Europe/Zurich offset at that time (check clock-change days)'] }))
    expect(f).toMatchObject({ place: 'fields' })
  })

  it('a 400 about rowVersion becomes a banner that does not tell the manager what to do', () => {
    const f = describeCorrectionFailure(e(400, 'request.invalid', { rowVersion: ['must be the rowVersion of the shift as last read'] }))
    expect(f.place).toBe('banner')
  })
})
