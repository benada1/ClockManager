import { ApiError, NetworkError } from '../../api/client.js'
import { fieldErrors, generalMessage } from '../../lib/errors.js'
import { resolveZurichWallTime } from '../../lib/zurichInput.js'

export const REASON_MIN = 5
export const REASON_MAX = 500

export const GAP_TEXT = "This time doesn't exist in Zurich (clocks jump forward)."
export const STALE_TEXT = 'This shift was changed meanwhile. We reloaded it; please check and try again.'
const TIME_DOWN = "We can't get a trusted time right now. Nothing was changed."

/** The resolved new time for the form: { status, iso?, choices? } (see resolveZurichWallTime). */
export function resolveForm(form) {
  if (!form.date || !form.time) return { status: 'empty' }
  const r = resolveZurichWallTime(form.date, form.time)
  if (r.status !== 'ambiguous') return r
  const chosen = r.choices.find((c) => c.offset === form.offset)
  return chosen ? { ...r, status: 'ok', iso: chosen.iso } : r
}

/**
 * Checks the form before sending. The server checks again (future, order, overlap).
 * Returns { ok: true, body } or { ok: false, errors: { field?, newTime?, offset?, reason? } }.
 */
export function buildCorrection(form, shift) {
  const errors = {}
  if (form.field !== 'clockIn' && form.field !== 'clockOut') errors.field = 'Choose which time to correct.'

  const r = resolveForm(form)
  if (r.status === 'empty') errors.newTime = 'Enter the date and the time.'
  else if (r.status === 'invalid') errors.newTime = 'Enter a real date and time.'
  else if (r.status === 'gap') errors.newTime = GAP_TEXT
  else if (r.status === 'ambiguous') errors.offset = 'This time happens twice that night. Choose which one you mean.'
  else if (!errors.field) {
    const current = shift?.[form.field]?.zurich
    if (current && Date.parse(current) === Date.parse(r.iso)) errors.newTime = 'This is already the current time. Enter the corrected time.'
  }

  const reason = (form.reason ?? '').trim()
  if (reason.length < REASON_MIN) errors.reason = `Write a reason of at least ${REASON_MIN} characters.`
  else if (reason.length > REASON_MAX) errors.reason = `The reason can be at most ${REASON_MAX} characters.`

  if (Object.keys(errors).length) return { ok: false, errors }
  return { ok: true, body: { field: form.field, newTime: r.iso, reason, rowVersion: shift.rowVersion } }
}

const FIELD_ERRORS = {
  'correction.overlap': 'This time overlaps another shift of this employee.',
  'correction.shift_still_running': 'This shift may still be running. You can only close a shift that has been open for a very long time.',
  'correction.in_future': 'This time is in the future. Choose a time that has already passed.',
  'correction.invalid_order': 'Clock out must be later than clock in.',
  'correction.unchanged': 'This is already the current time. Change it, or cancel.',
}

/**
 * Where and how a failed save is shown:
 * { place: 'fields', errors } | { place: 'stale', text } | { place: 'banner', title, text, retry }.
 */
export function describeCorrectionFailure(err) {
  if (err instanceof NetworkError) {
    return { place: 'banner', title: "We couldn't reach the server.", text: 'Try again: a change is never saved twice.', retry: true }
  }
  if (!(err instanceof ApiError)) return { place: 'banner', title: generalMessage(err), text: 'Nothing was changed.', retry: true }
  if (FIELD_ERRORS[err.code]) return { place: 'fields', errors: { newTime: FIELD_ERRORS[err.code] } }
  if (err.code === 'correction.stale') return { place: 'stale', text: STALE_TEXT }
  if (err.status === 503 || err.code?.startsWith('time.')) {
    return { place: 'banner', title: TIME_DOWN, text: 'The time service is unavailable. Try again.', retry: true }
  }
  if (err.status === 400) {
    const errors = fieldErrors(err)
    const known = Object.fromEntries(Object.entries(errors).filter(([k]) => ['newTime', 'reason', 'field'].includes(k)))
    if (Object.keys(known).length) return { place: 'fields', errors: known }
    return { place: 'banner', title: 'The server did not accept this change.', text: Object.values(errors).join(' ') || 'Nothing was changed.' }
  }
  if (err.status === 403 && err.code !== 'employee.inactive') return { place: 'banner', title: 'You need a manager account for this page.', text: 'Nothing was changed.' }
  if (err.status === 404) return { place: 'banner', title: 'We could not find this shift.', text: 'It may have been removed. Nothing was changed.' }
  return { place: 'banner', title: generalMessage(err), text: 'Nothing was changed.', retry: err.status >= 500 || err.status === 429 }
}

/**
 * Sends one correction. Only a 201 with a shift counts as saved; on a stale row version the
 * shift is reloaded so the form can show the new values.
 * -> { outcome: 'saved', detail } | { outcome: 'failed', failure, detail? }
 */
export async function submitCorrection({ api, shiftId, body }) {
  try {
    const detail = await api.postCorrection(shiftId, body)
    if (!detail?.shift) {
      return { outcome: 'failed', failure: { place: 'banner', title: 'The server answered in a way we did not expect.', text: 'Reload the page to see if the change was saved.' } }
    }
    return { outcome: 'saved', detail }
  } catch (err) {
    const failure = describeCorrectionFailure(err)
    if (failure.place !== 'stale') return { outcome: 'failed', failure }
    try {
      return { outcome: 'failed', failure, detail: await api.getShiftDetail(shiftId) }
    } catch {
      return { outcome: 'failed', failure: { place: 'banner', title: 'This shift was changed meanwhile.', text: 'We could not reload it. Close this and open the shift again.' } }
    }
  }
}
