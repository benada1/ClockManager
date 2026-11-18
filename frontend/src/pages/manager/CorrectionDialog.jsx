import { useEffect, useId, useRef, useState } from 'react'
import * as managerApi from '../../api/manager.js'
import Banner from '../../components/Banner.jsx'
import Button from '../../components/Button.jsx'
import Dialog from '../../components/Dialog.jsx'
import Field from '../../components/Field.jsx'
import Loading from '../../components/Loading.jsx'
import { needsOffset } from '../../lib/zurich.js'
import { toZurichInput } from '../../lib/zurichInput.js'
import { formatDayHeading } from '../history/weeks.js'
import { REASON_MAX, REASON_MIN, GAP_TEXT, buildCorrection, resolveForm, submitCorrection } from './correction.js'
import { FIELD_NAMES, originalValue, pointText } from './labels.js'

/** Date and time inputs start at the field's current value (an open shift's clock out starts on its clock-in day). */
function prefill(shift, field) {
  const current = shift?.[field]?.zurich
  if (current) return toZurichInput(current)
  return { date: toZurichInput(shift?.clockIn?.zurich).date, time: '', offset: null }
}

function Radio({ name, value, checked, onChange, children, describedBy }) {
  return (
    <label className="flex min-h-11 cursor-pointer items-center gap-3 rounded-ui border border-line-strong px-3 has-[:checked]:border-accent has-[:checked]:bg-accent-soft">
      <input type="radio" name={name} value={value} checked={checked} onChange={onChange} aria-describedby={describedBy} className="size-4 accent-accent" />
      <span className="text-base">{children}</span>
    </label>
  )
}

function Compare({ label, children, strong = false }) {
  return (
    <div className="flex flex-col">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className={`text-base ${strong ? 'font-semibold' : ''}`}>{children}</dd>
    </div>
  )
}

/**
 * Correct one time of a shift. Closes only after a 201 (onSaved gets the new detail). On a stale
 * row version the shift is reloaded here, the typed values are kept, and onChanged tells the page.
 */
export default function CorrectionDialog({ shiftId, initial = null, onClose, onSaved, onChanged, api = managerApi }) {
  const [detail, setDetail] = useState(initial)
  const [loadFailed, setLoadFailed] = useState(false)
  const [form, setForm] = useState(() => {
    const field = initial?.shift?.open ? 'clockOut' : 'clockIn'
    return { field, ...prefill(initial?.shift, field), reason: '' }
  })
  const [errors, setErrors] = useState({})
  const [failure, setFailure] = useState(null)
  const [busy, setBusy] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const inFlight = useRef(false)
  const formRef = useRef(null)
  const ids = { time: useId(), offset: useId(), reason: useId(), counter: useId() }

  // Opened from the week: load the detail (original values and a fresh row version) first.
  useEffect(() => {
    if (initial) return undefined
    const controller = new AbortController()
    api
      .getShiftDetail(shiftId, controller.signal)
      .then((d) => {
        setDetail(d)
        const field = d.shift.open ? 'clockOut' : 'clockIn'
        setForm((f) => ({ ...f, field, ...prefill(d.shift, field) }))
      })
      .catch((err) => err?.name !== 'AbortError' && setLoadFailed(true))
    return () => controller.abort()
  }, [api, initial, shiftId])

  // After a failed check, move focus to the first field that needs attention.
  useEffect(() => {
    if (attempt) formRef.current?.querySelector('[aria-invalid="true"]')?.focus()
  }, [attempt])

  const shift = detail?.shift
  const set = (patch, clear) => {
    setForm((f) => ({ ...f, ...patch }))
    setErrors((e) => ({ ...e, [clear]: undefined }))
  }
  const chooseField = (field) => {
    setForm((f) => ({ ...f, field, ...prefill(shift, field) }))
    setErrors((e) => ({ ...e, field: undefined, newTime: undefined, offset: undefined }))
  }

  async function submit(e) {
    e.preventDefault()
    if (inFlight.current || !shift) return
    const built = buildCorrection(form, shift)
    if (!built.ok) {
      setErrors(built.errors)
      setAttempt((n) => n + 1)
      return
    }
    inFlight.current = true
    setBusy(true)
    setFailure(null)
    const result = await submitCorrection({ api, shiftId, body: built.body })
    inFlight.current = false
    setBusy(false)
    if (result.outcome === 'saved') {
      onSaved(result.detail, form.field)
      return
    }
    if (result.detail) {
      setDetail(result.detail)
      onChanged?.(result.detail)
    }
    if (result.failure.place === 'fields') {
      setErrors(result.failure.errors)
      setAttempt((n) => n + 1)
    } else setFailure(result.failure)
  }

  const resolved = resolveForm(form)
  const current = shift?.[form.field]?.zurich ?? null
  const original = originalValue(detail?.corrections, form.field)
  const withOffset = needsOffset(current, original.iso, resolved.iso)
  const ambiguous = resolveForm({ ...form, offset: null })
  const reasonLength = form.reason.trim().length
  const timeError = errors.newTime ?? (resolved.status === 'gap' ? GAP_TEXT : undefined)

  return (
    <Dialog
      title="Correct shift"
      description={detail ? `${detail.employee?.displayName ?? ''} · ${formatDayHeading(shift.workDate)}` : null}
      onClose={onClose}
      locked={busy}
    >
      {!detail && !loadFailed && <Loading>Loading the shift…</Loading>}
      {loadFailed && (
        <>
          <Banner tone="error" title="We could not load this shift.">
            Close this and try again.
          </Banner>
          <Button variant="secondary" onClick={onClose} className="self-start">
            Close
          </Button>
        </>
      )}

      {detail && (
        <form ref={formRef} noValidate onSubmit={submit} className="flex flex-col gap-6">
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-2 text-base font-medium">Which time?</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {['clockIn', 'clockOut'].map((f) => (
                <Radio key={f} name="field" value={f} checked={form.field === f} onChange={() => chooseField(f)}>
                  {FIELD_NAMES[f]}
                  <span className="block text-sm text-muted">
                    {shift[f]?.zurich ? `Now ${pointText(shift[f].zurich, { sameDayAs: shift.clockIn.zurich })}` : 'Not clocked out yet'}
                  </span>
                </Radio>
              ))}
            </div>
            {errors.field && <p className="text-sm font-medium text-danger">{errors.field}</p>}
          </fieldset>

          <dl className="grid grid-cols-3 gap-4 rounded-ui bg-canvas p-4">
            <Compare label="Current">{current ? pointText(current, { withOffset }) : 'Still open'}</Compare>
            <Compare label="Original">
              {!original.changed ? 'Same as current' : original.iso ? pointText(original.iso, { withOffset, sameDayAs: current }) : 'Still open'}
            </Compare>
            <Compare label="New" strong>
              {resolved.status === 'ok' ? pointText(resolved.iso, { withOffset, sameDayAs: current }) : '–'}
            </Compare>
          </dl>

          <div className="flex flex-col gap-2">
            <div className="grid grid-cols-2 gap-4">
              <Field
                label="Date (Zurich)"
                type="date"
                value={form.date}
                onChange={(e) => set({ date: e.target.value, offset: null }, 'newTime')}
                aria-invalid={timeError ? true : undefined}
                aria-describedby={timeError ? ids.time : undefined}
                required
              />
              <Field
                label="Time (Zurich)"
                type="time"
                value={form.time}
                onChange={(e) => set({ time: e.target.value, offset: null }, 'newTime')}
                aria-invalid={timeError ? true : undefined}
                aria-describedby={timeError ? ids.time : undefined}
                required
              />
            </div>
            {timeError && (
              <p id={ids.time} className="text-sm font-medium text-danger">
                {timeError}
              </p>
            )}
          </div>

          {ambiguous.status === 'ambiguous' && (
            <fieldset className="flex flex-col gap-2">
              <legend className="mb-2 text-base font-medium">This time happens twice that night. Which one?</legend>
              {ambiguous.choices.map((c) => (
                <Radio
                  key={c.offset}
                  name="offset"
                  value={c.offset}
                  checked={form.offset === c.offset}
                  onChange={() => set({ offset: c.offset }, 'offset')}
                  describedBy={errors.offset ? ids.offset : undefined}
                >
                  {c.label}
                </Radio>
              ))}
              {errors.offset && (
                <p id={ids.offset} className="text-sm font-medium text-danger">
                  {errors.offset}
                </p>
              )}
            </fieldset>
          )}

          <div className="flex flex-col gap-1">
            <label htmlFor={ids.reason} className="text-base font-medium">
              Reason
            </label>
            <textarea
              id={ids.reason}
              rows={3}
              maxLength={REASON_MAX}
              value={form.reason}
              onChange={(e) => set({ reason: e.target.value }, 'reason')}
              aria-invalid={errors.reason ? true : undefined}
              aria-describedby={[ids.counter, errors.reason && `${ids.reason}-error`].filter(Boolean).join(' ')}
              required
              className={`w-full rounded-ui border bg-surface px-3 py-2 text-base text-ink ${errors.reason ? 'border-danger-line' : 'border-line-strong'}`}
            />
            <p id={ids.counter} className="text-sm text-muted">
              Required, {REASON_MIN} to {REASON_MAX} characters. It is saved with the change. ({reasonLength} / {REASON_MAX})
            </p>
            {errors.reason && (
              <p id={`${ids.reason}-error`} className="text-sm font-medium text-danger">
                {errors.reason}
              </p>
            )}
          </div>

          <div>
            <p role="status" className="sr-only">
              {failure?.place === 'stale' ? failure.text : ''}
            </p>
            {failure && (
              <Banner
                tone={failure.place === 'stale' ? 'warning' : 'error'}
                title={failure.place === 'stale' ? failure.text : failure.title}
                action={
                  failure.retry && (
                    <Button type="submit" variant="secondary" busy={busy} busyLabel="Trying again…" className="self-start">
                      Try again
                    </Button>
                  )
                }
              >
                {failure.place === 'stale' ? null : failure.text}
              </Banner>
            )}
          </div>

          <div className="flex flex-wrap justify-end gap-3">
            <Button variant="secondary" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button type="submit" busy={busy} busyLabel="Saving…">
              Save correction
            </Button>
          </div>
        </form>
      )}
    </Dialog>
  )
}
