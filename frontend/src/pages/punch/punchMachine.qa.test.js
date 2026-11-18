// QA (phase 8): punch machine sequences the first test pass did not cover.
import { describe, expect, it } from 'vitest'
import { ApiError, NetworkError } from '../../api/client.js'
import describeFailure from './describeFailure.js'
import { createPunchController } from './punchMachine.js'

const at = (t) => ({ utc: `2026-11-18T${t}:00.000Z`, zurich: `2026-11-18T${t}:00.000+01:00` })
const openShift = { shiftId: 7, clockIn: at('07:00'), clockOut: null }
const closedShift = { shiftId: 7, clockIn: at('07:00'), clockOut: at('16:00'), durationSeconds: 32400 }

/** A fake server whose state really changes when a punch "arrives", even if the answer is then lost. */
function world({ loseAnswerOf }) {
  let open = null
  let last = null
  const punches = []
  let lost = false
  const c = createPunchController({
    api: {
      getStatus: () => Promise.resolve({ openShift: open, lastClosedShift: last, serverNow: null, flags: [] }),
      punch: (intent, key) => {
        const replay = punches.some((p) => p.key === key)
        punches.push({ intent, key })
        if (!replay) {
          if (intent === 'clock-in') open = openShift
          else [open, last] = [null, closedShift]
        }
        if (intent === loseAnswerOf && !lost) {
          lost = true
          return Promise.reject(new NetworkError('response lost'))
        }
        return Promise.resolve(intent === 'clock-in' ? openShift : closedShift)
      },
    },
    newKey: (() => {
      let n = 0
      return () => `key-${++n}`
    })(),
    now: () => 0,
  })
  return { c, punches }
}

describe('QA-27: "Try again" after a lost answer', () => {
  // The server saved the clock-in, the answer got lost, the automatic refresh then shows "clocked in" next to a banner that
  // still offers "Try again: it will not be recorded twice". That button calls press(), which derives the intent from the NEW status.
  it('QA-27: Try again repeats the SAME intent and key (a replay), it does not clock out', async () => {
    const { c, punches } = world({ loseAnswerOf: 'clock-in' })
    await c.refresh()
    await c.press() // clock-in: saved on the server, answer lost, refresh follows
    expect(c.getState().problem.retry).toBe(true)
    expect(Boolean(c.getState().status.openShift)).toBe(true)
    await c.press() // what the banner's "Try again" button does
    expect(punches[1]).toEqual({ intent: 'clock-in', key: punches[0].key })
  })

  it('QA-27: the same for clock-out: Try again must not start a new shift', async () => {
    const { c, punches } = world({ loseAnswerOf: 'clock-out' })
    await c.refresh()
    await c.press() // clock-in ok
    await c.press() // clock-out: saved, answer lost, refresh shows "clocked out"
    expect(c.getState().problem.retry).toBe(true)
    expect(Boolean(c.getState().status.openShift)).toBe(false)
    await c.press() // "Try again"
    expect(punches[2]).toEqual({ intent: 'clock-out', key: punches[1].key })
  })
})

describe('QA-31: the idempotency key cannot be made (crypto.randomUUID needs https or localhost)', () => {
  it('QA-31: a throwing key factory must not leave press() rejecting without any message', async () => {
    const c = createPunchController({
      api: { getStatus: () => Promise.resolve({ openShift: null, lastClosedShift: null, serverNow: null, flags: [] }), punch: () => Promise.reject(new Error('never')) },
      newKey: () => {
        throw new TypeError('crypto.randomUUID is not a function')
      },
      now: () => 0,
    })
    await c.refresh()
    await expect(c.press()).resolves.toBeUndefined()
    expect(c.getState().problem).toBeTruthy()
  })
})

describe('pins: a retried punch after a 503 keeps its key, a different intent does not', () => {
  it('503 then retry with the same status sends the same key', async () => {
    const keys = []
    let fail = true
    const c = createPunchController({
      api: {
        getStatus: () => Promise.resolve({ openShift: null, lastClosedShift: null, serverNow: null, flags: [] }),
        punch: (_intent, key) => {
          keys.push(key)
          if (fail) {
            fail = false
            return Promise.reject(new ApiError({ status: 503, code: 'time.unavailable' }))
          }
          return Promise.resolve(openShift)
        },
      },
      newKey: (() => {
        let n = 0
        return () => `k${++n}`
      })(),
      now: () => 0,
    })
    await c.refresh()
    await c.press()
    await c.press()
    expect(keys).toEqual(['k1', 'k1'])
  })
})

describe('QA-40: 409 shift.already_open without an open shift', () => {
  it('does not claim "already clocked in"', () => {
    const p = describeFailure(new ApiError({ status: 409, code: 'shift.already_open', problem: { openShift: null } }))
    expect(p.title).not.toMatch(/already clocked in/i)
    expect(p.title).toMatch(/another tab/i)
  })
  it('keeps the old wording when the server sends the open shift', () => {
    const p = describeFailure(new ApiError({ status: 409, code: 'shift.already_open', problem: { openShift } }))
    expect(p.title).toMatch(/already clocked in/i)
  })
})
