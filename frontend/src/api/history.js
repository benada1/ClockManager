import { apiGet } from './client.js'

/** No range = the server's default week (the week of my latest shift). Dates are Zurich yyyy-MM-dd. */
export const getShifts = (range, signal) => {
  const query = range ? `?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}` : ''
  return apiGet(`/me/shifts${query}`, { signal })
}
