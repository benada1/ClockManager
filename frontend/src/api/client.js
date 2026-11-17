// The app's only HTTP layer. Components call the small per-feature modules in src/api/,
// never fetch() directly. Requests go to /api, which Vite proxies to the backend in dev.

const BASE_URL = '/api'

// Set by the dev-server proxy (vite.config.js) when the backend cannot be reached,
// so a stopped API is reported as a network problem rather than a generic 502.
export const PROXY_UNREACHABLE_HEADER = 'x-dev-proxy-error'

/** The server answered with an error status. Fields come from RFC 7807 ProblemDetails when present. */
export class ApiError extends Error {
  constructor({ status, code = null, title = null, detail = null, traceId = null, errors = null, problem = null }) {
    super(detail || title || `Request failed with HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.title = title
    this.detail = detail
    this.traceId = traceId
    this.errors = errors // model-validation errors: { field: [messages] }
    this.problem = problem
  }
}

/** The request never got an answer from the API (offline, API stopped, DNS, CORS...). */
export class NetworkError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'NetworkError'
  }
}

async function readBody(response) {
  if (response.status === 204) return null
  const text = await response.text()
  if (!text) return null
  const type = response.headers.get('content-type') || ''
  if (!type.includes('json')) return text
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

function toApiError(response, body) {
  const problem = body && typeof body === 'object' ? body : null
  return new ApiError({
    status: response.status,
    code: problem?.code ?? null,
    title: problem?.title ?? (response.statusText || null),
    detail: problem?.detail ?? null,
    traceId: problem?.traceId ?? null,
    errors: problem?.errors ?? null,
    problem,
  })
}

/**
 * Sends a request to the API and returns the parsed JSON body.
 * Throws ApiError for non-2xx answers, NetworkError when there is no answer,
 * and rethrows AbortError untouched so callers can ignore cancelled requests.
 */
export async function apiRequest(path, { method = 'GET', body, signal, headers } = {}) {
  const hasBody = body !== undefined
  let response
  try {
    response = await fetch(BASE_URL + path, {
      method,
      signal,
      headers: {
        Accept: 'application/json, application/problem+json',
        ...(hasBody ? { 'Content-Type': 'application/json' } : {}),
        ...headers,
      },
      body: hasBody ? JSON.stringify(body) : undefined,
    })
  } catch (err) {
    if (err?.name === 'AbortError') throw err
    throw new NetworkError('Could not reach the API.', { cause: err })
  }

  if (response.headers.get(PROXY_UNREACHABLE_HEADER)) {
    throw new NetworkError('The API is not running or not reachable.')
  }

  const payload = await readBody(response)
  if (!response.ok) throw toApiError(response, payload)
  if (payload !== null && typeof payload !== 'object') {
    throw new ApiError({ status: response.status, code: 'client.unexpected_body', title: 'Expected a JSON response' })
  }
  return payload
}

export const apiGet = (path, options) => apiRequest(path, { ...options, method: 'GET' })
export const apiPost = (path, body, options) => apiRequest(path, { ...options, method: 'POST', body })
