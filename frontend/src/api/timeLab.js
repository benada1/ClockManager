import { apiGet } from './client.js'

/**
 * Development-only diagnostic: asks every configured time provider once (on the server)
 * and returns each answer, the cross-check and the trusted clock's decision.
 * 404 when the backend does not run in Development.
 */
export function getTimeLab({ signal } = {}) {
  return apiGet('/dev/time-lab', { signal })
}
