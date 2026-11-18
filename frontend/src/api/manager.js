import { apiGet, apiPost } from './client.js'

// Contract: plans/07-manager.md section 5. Every call needs a Manager login (403 auth.forbidden otherwise).
export const getEmployees = (signal) => apiGet('/manager/employees', { signal })
export const getAnomalies = (signal) => apiGet('/manager/anomalies', { signal })

/** Same body as GET /api/me/shifts; no range = the server's default week. */
export const getEmployeeShifts = (employeeId, range, signal) => {
  const query = range ? `?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}` : ''
  return apiGet(`/manager/employees/${encodeURIComponent(employeeId)}/shifts${query}`, { signal })
}

export const getShiftDetail = (shiftId, signal) => apiGet(`/manager/shifts/${encodeURIComponent(shiftId)}`, { signal })

/** body: { field: 'clockIn' | 'clockOut', newTime (Zurich ISO with offset), reason, rowVersion }. 201 = new shift detail. */
export const postCorrection = (shiftId, body) => apiPost(`/manager/shifts/${encodeURIComponent(shiftId)}/corrections`, body)
