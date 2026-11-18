import { ApiError, NetworkError } from '../api/client.js'

/** First message per field from a 400 `errors` object (keys become camelCase). */
export function fieldErrors(err) {
  const out = {}
  if (!(err instanceof ApiError) || !err.errors) return out
  for (const [key, messages] of Object.entries(err.errors)) {
    const text = Array.isArray(messages) ? messages[0] : messages
    if (text) out[key.charAt(0).toLowerCase() + key.slice(1)] = text
  }
  return out
}

/** A plain sentence for problems that are not tied to one field. */
export function generalMessage(err) {
  if (err instanceof NetworkError) return 'You are offline or the server is down. Check your connection and try again.'
  if (err instanceof ApiError) {
    if (err.status === 429) return 'Too many tries. Wait a minute, then try again.'
    if (err.status === 403 && err.code === 'employee.inactive') return 'This account is switched off. Ask your manager.'
    if (err.status >= 500) return 'Something went wrong on our side. Nothing was changed. Try again in a moment.'
  }
  return 'Something went wrong. Try again.'
}
