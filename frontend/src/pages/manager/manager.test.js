import { describe, expect, it } from 'vitest'
import { ApiError, NetworkError } from '../../api/client.js'
import { resolveZurichWallTime, toZurichInput } from '../../lib/zurichInput.js'
import { shiftBadges } from '../history/labels.js'
import { GAP_TEXT, STALE_TEXT, buildCorrection, describeCorrectionFailure, submitCorrection } from './correction.js'
import { anomalyText, correctionSentence, employeeStatusText, originalText } from './labels.js'
import { createLoader } from './useLoad.js'

const shift = {
  shiftId: 102,
  workDate: '2026-11-17',
  clockIn: { zurich: '2026-11-17T08:03:11+01:00' },
  clockOut: { zurich: '2026-11-17T17:12:20+01:00' },
  open: false,
  rowVersion: 'AAAAAAAAB9E=',
}
const form = (patch = {}) => ({ field: 'clockOut', date: '2026-11-17', time: '18:05', offset: null, reason: 'Stayed for the delivery', ...patch })
const apiError = (status, code, extra = {}) => new ApiError({ status, code, ...extra })

describe('Zurich wall time to an ISO instant', () => {
  it('uses +01:00 in winter and +02:00 in summer (T1, T2)', () => {
    expect(resolveZurichWallTime('2026-01-12', '08:00')).toEqual({ status: 'ok', iso: '2026-01-12T08:00:00+01:00' })
    expect(resolveZurichWallTime('2026-07-06', '08:00')).toEqual({ status: 'ok', iso: '2026-07-06T08:00:00+02:00' })
  })

  it('offers both moments inside the repeated October hour, summer time first (T13)', () => {
    const r = resolveZurichWallTime('2026-10-25', '02:30')
    expect(r.status).toBe('ambiguous')
    expect(r.choices.map((c) => c.iso)).toEqual(['2026-10-25T02:30:00+02:00', '2026-10-25T02:30:00+01:00'])
    expect(r.choices[0].label).toBe('First time (summer time, UTC+2)')
    expect(r.choices[1].label).toBe('Second time (winter time, UTC+1)')
  })

  it('finds no moment in the skipped March hour (T12)', () => {
    expect(resolveZurichWallTime('2026-03-29', '02:30')).toEqual({ status: 'gap' })
    expect(resolveZurichWallTime('2027-03-28', '02:00')).toEqual({ status: 'gap' })
  })

  it('is exact at the edges of both clock changes (T7)', () => {
    expect(resolveZurichWallTime('2026-03-29', '01:59').iso).toBe('2026-03-29T01:59:00+01:00')
    expect(resolveZurichWallTime('2026-03-29', '03:00').iso).toBe('2026-03-29T03:00:00+02:00')
    expect(resolveZurichWallTime('2026-10-25', '01:59').iso).toBe('2026-10-25T01:59:00+02:00')
    expect(resolveZurichWallTime('2026-10-25', '03:00').iso).toBe('2026-10-25T03:00:00+01:00')
  })

  it('handles midnight and the year boundary (T3, T8)', () => {
    expect(resolveZurichWallTime('2026-12-31', '23:59').iso).toBe('2026-12-31T23:59:00+01:00')
    expect(resolveZurichWallTime('2027-01-01', '00:00').iso).toBe('2027-01-01T00:00:00+01:00')
  })

  it('refuses dates and times that are not real', () => {
    for (const [d, t] of [['2026-02-30', '08:00'], ['2026-11-17', '24:00'], ['17.11.2026', '08:00'], ['', '']]) {
      expect(resolveZurichWallTime(d, t)).toEqual({ status: 'invalid' })
    }
  })

  it('turns a server value back into the inputs, with its offset', () => {
    expect(toZurichInput('2026-10-25T01:15:00Z')).toEqual({ date: '2026-10-25', time: '02:15', offset: '+01:00' })
    expect(toZurichInput('2026-10-25T02:30:00+02:00')).toEqual({ date: '2026-10-25', time: '02:30', offset: '+02:00' })
    expect(toZurichInput(null)).toEqual({ date: '', time: '', offset: null })
  })
})

describe('correction form', () => {
  it('builds the body with the Zurich offset and the loaded row version', () => {
    expect(buildCorrection(form({ reason: '  Stayed for the delivery  ' }), shift)).toEqual({
      ok: true,
      body: { field: 'clockOut', newTime: '2026-11-17T18:05:00+01:00', reason: 'Stayed for the delivery', rowVersion: 'AAAAAAAAB9E=' },
    })
  })

  it('checks the reason length (5 to 500, trimmed)', () => {
    expect(buildCorrection(form({ reason: '  abc  ' }), shift).errors.reason).toMatch(/at least 5/)
    expect(buildCorrection(form({ reason: 'x'.repeat(501) }), shift).errors.reason).toMatch(/at most 500/)
  })

  it('needs a choice in the repeated hour and refuses the skipped hour', () => {
    const october = { ...shift, workDate: '2026-10-25', clockIn: { zurich: '2026-10-25T01:00:00+02:00' }, clockOut: { zurich: '2026-10-25T04:00:00+01:00' } }
    expect(buildCorrection(form({ date: '2026-10-25', time: '02:30' }), october).errors.offset).toMatch(/twice/)
    expect(buildCorrection(form({ date: '2026-10-25', time: '02:30', offset: '+01:00' }), october).body.newTime).toBe('2026-10-25T02:30:00+01:00')
    expect(buildCorrection(form({ date: '2026-03-29', time: '02:30' }), shift).errors.newTime).toBe(GAP_TEXT)
  })

  it('refuses an unchanged time and a missing field choice', () => {
    expect(buildCorrection(form({ time: '17:12' }), { ...shift, clockOut: { zurich: '2026-11-17T17:12:00+01:00' } }).errors.newTime).toMatch(/already/)
    expect(buildCorrection(form({ field: null }), shift).errors.field).toBeTruthy()
  })
})

describe('saving a correction', () => {
  const body = { field: 'clockOut', newTime: '2026-11-17T18:05:00+01:00', reason: 'Stayed late', rowVersion: 'v1' }

  it('counts only a 201 with a shift as saved', async () => {
    const saved = { shift: { ...shift, rowVersion: 'v2' }, corrections: [] }
    expect(await submitCorrection({ api: { postCorrection: async () => saved }, shiftId: 102, body })).toEqual({ outcome: 'saved', detail: saved })
    const odd = await submitCorrection({ api: { postCorrection: async () => ({}) }, shiftId: 102, body })
    expect(odd.outcome).toBe('failed')
  })

  it('reloads the shift after a stale row version and keeps the form open', async () => {
    const fresh = { shift: { ...shift, rowVersion: 'v9' }, corrections: [] }
    const asked = []
    const r = await submitCorrection({
      api: { postCorrection: async () => Promise.reject(apiError(409, 'correction.stale')), getShiftDetail: async (id) => (asked.push(id), fresh) },
      shiftId: 102,
      body,
    })
    expect(r).toEqual({ outcome: 'failed', failure: { place: 'stale', text: STALE_TEXT }, detail: fresh })
    expect(asked).toEqual([102])
  })

  it('puts rule refusals next to the time field and 400 messages next to their fields', () => {
    expect(describeCorrectionFailure(apiError(409, 'correction.overlap')).errors.newTime).toMatch(/overlaps/)
    expect(describeCorrectionFailure(apiError(409, 'correction.shift_still_running')).errors.newTime).toMatch(/still be running/)
    expect(describeCorrectionFailure(apiError(422, 'correction.in_future')).errors.newTime).toMatch(/future/)
    expect(describeCorrectionFailure(apiError(422, 'correction.unchanged')).errors.newTime).toMatch(/already the current time/)
    expect(describeCorrectionFailure(apiError(422, 'correction.invalid_order')).errors.newTime).toMatch(/later than clock in/)
    const invalid = apiError(400, 'request.invalid', { errors: { Reason: ['must be 5 to 500 characters'] } })
    expect(describeCorrectionFailure(invalid)).toEqual({ place: 'fields', errors: { reason: 'must be 5 to 500 characters' } })
  })

  it('shows the trusted-time banner on 503 and offers a retry when offline', () => {
    const down = describeCorrectionFailure(apiError(503, 'time.unavailable'))
    expect(down).toMatchObject({ place: 'banner', retry: true })
    expect(down.title).toMatch(/trusted time/)
    expect(describeCorrectionFailure(new NetworkError('x'))).toMatchObject({ place: 'banner', retry: true })
    expect(describeCorrectionFailure(apiError(403, 'auth.forbidden')).title).toBe('You need a manager account for this page.')
  })
})

describe('manager page loader', () => {
  it('keeps the shown list when a reload fails and names a 403 plainly', async () => {
    let fail = false
    const c = createLoader(() => (fail ? Promise.reject(apiError(403, 'auth.forbidden')) : Promise.resolve(['a'])))
    await c.reload()
    fail = true
    await c.reload()
    expect(c.getState().data).toEqual(['a'])
    expect(c.getState().failure.kind).toBe('forbidden')
  })
})

describe('manager texts', () => {
  const by = { id: 4, displayName: 'Maria Manager' }
  const at = { zurich: '2026-11-18T09:40:00+01:00' }

  it('writes a change in plain words with who and when', () => {
    const c = { field: 'clockOut', old: { zurich: '2026-11-17T17:12:00+01:00' }, new: { zurich: '2026-11-17T18:05:00+01:00' }, by, at }
    expect(correctionSentence(c)).toBe('Clock out changed from 17:12 to 18:05 (Zurich time) by Maria Manager on Wed 18 Nov, 09:40.')
    const closed = { field: 'ClockOut', old: null, new: { zurich: '2026-11-18T01:30:00+01:00' }, by, at }
    expect(correctionSentence(closed)).toBe('Clock out set to 01:30 (Zurich time) by Maria Manager on Wed 18 Nov, 09:40. Before, the shift was still open.')
  })

  it('shows offsets for a change inside the repeated hour', () => {
    const c = { field: 'clockIn', old: { zurich: '2026-10-25T02:30:00+02:00' }, new: { zurich: '2026-10-25T02:30:00+01:00' }, by, at }
    expect(correctionSentence(c)).toBe('Clock in changed from 02:30 UTC+2 to 02:30 UTC+1 (Zurich time) by Maria Manager on Wed 18 Nov, 09:40.')
  })

  it('keeps the original value visible from the first correction', () => {
    const list = [
      { field: 'clockOut', old: { zurich: '2026-11-17T16:00:00+01:00' }, new: { zurich: '2026-11-17T17:00:00+01:00' } },
      { field: 'clockOut', old: { zurich: '2026-11-17T17:00:00+01:00' }, new: { zurich: '2026-11-17T17:12:00+01:00' } },
    ]
    expect(originalText(list, 'clockOut', '2026-11-17T17:12:00+01:00')).toBe('Original: 16:00')
    expect(originalText(list, 'clockIn', '2026-11-17T08:00:00+01:00')).toBeNull()
    expect(originalText([{ field: 'clockOut', old: null, new: {} }], 'clockOut', null)).toBe('Original: still open')
  })

  it('labels status, problems and corrected shifts in plain words', () => {
    expect(employeeStatusText({ status: 'clocked_in', since: { zurich: '2026-11-18T08:03:11+01:00' } })).toBe('Clocked in since Wed 18 Nov, 08:03')
    expect(employeeStatusText({ status: 'clocked_out', since: null })).toBe('Not clocked in')
    expect(anomalyText('forgotten_clock_out')).toBe('Forgot to clock out')
    expect(shiftBadges({ corrected: true, flags: [] }).map((b) => b.text)).toEqual(['Corrected by a manager'])
    expect(shiftBadges({ corrected: true, flags: [] }, { manager: true }).map((b) => b.text)).toEqual(['Corrected'])
  })
})
