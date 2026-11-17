import { apiGet } from './client.js'

/** Liveness of the API process itself (not of the time providers). */
export function getHealth({ signal } = {}) {
  return apiGet('/health', { signal })
}
