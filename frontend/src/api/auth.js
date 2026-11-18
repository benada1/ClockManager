import { apiGet, apiPost } from './client.js'

// Contract: plans/04-auth.md section 3. The login cookie is set and cleared by the server.
export const signup = (body) => apiPost('/auth/signup', body)
export const login = (body) => apiPost('/auth/login', body)
export const logout = () => apiPost('/auth/logout')
export const getMe = ({ signal } = {}) => apiGet('/auth/me', { signal })
