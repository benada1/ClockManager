import { apiGet, apiPost } from './client.js'

/** The body is only the intent; the server takes the time itself. Same key = same attempt (safe retry). */
export const punch = (intent, idempotencyKey) =>
  apiPost('/punches', { intent }, { headers: { 'Idempotency-Key': idempotencyKey } })

export const getStatus = ({ signal } = {}) => apiGet('/me/status', { signal })
