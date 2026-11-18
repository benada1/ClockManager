// QA (phase 8): how the API layer behaves when the answer is not the neat ProblemDetails the backend normally sends.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, NetworkError, apiGet, apiPost, onSessionEnded } from './client.js'

const answer = (body, init = {}) => vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(body, init))
const json = (obj, status, extra = {}) =>
  answer(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/problem+json', ...extra } })

afterEach(() => {
  vi.restoreAllMocks()
  onSessionEnded(() => {})
})

describe('answers that are not ProblemDetails', () => {
  it('an HTML 502 from a proxy becomes an ApiError without a code', async () => {
    answer('<html><body>502 Bad Gateway</body></html>', { status: 502, statusText: 'Bad Gateway', headers: { 'Content-Type': 'text/html' } })
    const err = await apiGet('/me/status').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(502)
    expect(err.code).toBeNull()
    expect(err.problem).toBeNull()
  })

  it('a 500 with an empty body is an ApiError', async () => {
    answer('', { status: 500 })
    const err = await apiGet('/me/status').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(500)
  })

  it('a 500 that says it is JSON but is not parseable is an ApiError, not a SyntaxError', async () => {
    answer('{oops', { status: 500, headers: { 'Content-Type': 'application/json' } })
    const err = await apiGet('/x').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
  })

  it('a 204 resolves to null', async () => {
    answer(null, { status: 204 })
    await expect(apiPost('/auth/logout')).resolves.toBeNull()
  })

  it('a 200 with an HTML body (SPA fallback answering /api) is refused, not trusted', async () => {
    answer('<!doctype html><html></html>', { status: 200, headers: { 'Content-Type': 'text/html' } })
    const err = await apiGet('/auth/me').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.code).toBe('client.unexpected_body')
  })

  it('a fetch that rejects is a NetworkError; an abort stays an AbortError', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await expect(apiGet('/x')).rejects.toBeInstanceOf(NetworkError)
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(Object.assign(new Error('aborted'), { name: 'AbortError' }))
    await expect(apiGet('/x')).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('the dev proxy marker turns a 502 into a NetworkError', async () => {
    json({ code: 'dev.backend_unreachable' }, 502, { 'X-Dev-Proxy-Error': 'backend-unreachable' })
    await expect(apiGet('/x')).rejects.toBeInstanceOf(NetworkError)
  })
})

describe('what ends the session', () => {
  it('401 auth.unauthenticated calls the handler; wrong credentials and 403 do not', async () => {
    const ended = vi.fn()
    onSessionEnded(ended)
    json({ code: 'auth.invalid_credentials' }, 401)
    await apiPost('/auth/login', {}).catch(() => {})
    json({ code: 'auth.forbidden' }, 403)
    await apiGet('/manager/employees').catch(() => {})
    json({ code: 'employee.inactive' }, 403)
    await apiGet('/me/status').catch(() => {})
    expect(ended).not.toHaveBeenCalled()
    json({ code: 'auth.unauthenticated' }, 401)
    await apiGet('/me/status').catch(() => {})
    expect(ended).toHaveBeenCalledTimes(1)
  })
})

describe('what is sent', () => {
  it('a punch body holds only the intent; no time and no cookie token in JavaScript', async () => {
    const spy = json({ shiftId: 1 }, 201)
    spy.mockResolvedValue(new Response('{"shiftId":1}', { status: 201, headers: { 'Content-Type': 'application/json' } }))
    const { punch } = await import('./punches.js')
    await punch('clock-in', 'key-1')
    const [url, init] = spy.mock.calls[0]
    expect(url).toBe('/api/punches')
    expect(JSON.parse(init.body)).toEqual({ intent: 'clock-in' })
    expect(init.headers['Idempotency-Key']).toBe('key-1')
    expect(init.credentials).toBe('same-origin')
    expect(Object.keys(init.headers).map((h) => h.toLowerCase())).not.toContain('authorization')
  })

  it('ids and dates in the URL are encoded', async () => {
    const spy = answer('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    const { getEmployeeShifts } = await import('./manager.js')
    await getEmployeeShifts('1/../2', { from: '2026-11-16&x=1', to: '2026-11-22' })
    expect(spy.mock.calls[0][0]).toBe('/api/manager/employees/1%2F..%2F2/shifts?from=2026-11-16%26x%3D1&to=2026-11-22')
  })
})
