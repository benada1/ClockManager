import { ApiError, NetworkError } from '../../api/client.js'
import { generalMessage } from '../../lib/errors.js'

const TIME_DOWN = "We can't get a trusted time right now. Nothing was recorded."

/** Maps a failed punch to what the screen says and whether the same attempt (same key) can be retried. */
export default function describeFailure(err) {
  if (err instanceof NetworkError) {
    return {
      title: "We couldn't reach the server.",
      text: 'Your punch may or may not have been saved. Try again: it will not be recorded twice.',
      retry: true,
    }
  }
  if (!(err instanceof ApiError)) return { title: generalMessage(err), retry: true }
  switch (err.code) {
    case 'shift.already_open':
      return { tone: 'warning', title: 'You are already clocked in.', text: 'This can happen when you use another tab. We updated this page and recorded nothing new.' }
    case 'shift.not_open':
      return { tone: 'warning', title: 'You are not clocked in any more.', text: 'This can happen when you use another tab. We updated this page and recorded nothing new.' }
    case 'time.providers_disagree':
      return { title: TIME_DOWN, text: 'The time services do not agree with each other. Try again in a moment.', retry: true }
    case 'time.went_backwards':
      return { title: TIME_DOWN, text: 'The time we got is not later than your last punch. Try again in a moment.', retry: true }
    case 'time.unavailable':
    case 'time.invalid_response':
      return { title: TIME_DOWN, text: 'The time service is unavailable. Try again.', retry: true }
    case 'idempotency.key_reused':
      return { title: 'That attempt could not be sent again.', text: 'Nothing was recorded. Press the button once more.' }
    case 'request.invalid': {
      const messages = Object.values(err.errors ?? {}).flat()
      return { title: 'The server did not accept this punch.', text: messages.join(' ') || 'Nothing was recorded. Try again.' }
    }
    default:
      if (err.status === 429) return { title: 'Too many tries.', text: 'Wait a minute, then try again.', retry: true }
      if (err.status === 403) return { title: generalMessage(err), text: 'Nothing was changed.' }
      return { title: generalMessage(err), text: 'Nothing was changed.', retry: true }
  }
}

/** The 201 body must be the shift we asked for: clock-in leaves it open, clock-out closes it. */
export function isExpectedShift(intent, shift) {
  if (!shift || typeof shift !== 'object' || shift.shiftId == null || typeof shift.clockIn?.zurich !== 'string') return false
  return intent === 'clock-in' ? shift.clockOut == null : typeof shift.clockOut?.zurich === 'string'
}

export const UNEXPECTED_ANSWER = {
  title: 'The server answered in a way we did not expect.',
  text: 'This screen was not changed. Press Try again: it will not be recorded twice.',
  retry: true,
}
