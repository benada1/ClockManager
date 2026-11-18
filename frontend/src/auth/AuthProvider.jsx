import { useCallback, useEffect, useMemo, useState } from 'react'
import * as authApi from '../api/auth.js'
import { NetworkError, onSessionEnded } from '../api/client.js'
import { AuthContext } from './authContext.js'

// phase: 'loading' (asking /me) | 'in' | 'out' | 'unreachable' (could not ask)
export default function AuthProvider({ children }) {
  const [state, setState] = useState({ phase: 'loading', user: null, notice: null })

  // The first load starts as "loading" already; only a retry has to switch back to it.
  const load = useCallback((signal) => {
    authApi
      .getMe({ signal })
      .then((user) => setState({ phase: 'in', user, notice: null }))
      .catch((err) => {
        if (err.name === 'AbortError') return
        if (err instanceof NetworkError || err.status >= 500) setState({ phase: 'unreachable', user: null, notice: null })
        else setState({ phase: 'out', user: null, notice: null })
      })
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal)
    return () => controller.abort()
  }, [load])

  // Any later 401 means the session ended: back to login with an explanation.
  useEffect(() => {
    onSessionEnded(() =>
      setState((s) =>
        s.phase === 'in' ? { phase: 'out', user: null, notice: 'Your session ended, please log in.' } : s,
      ),
    )
  }, [])

  const value = useMemo(
    () => ({
      ...state,
      retry: () => {
        setState((s) => ({ ...s, phase: 'loading' }))
        load()
      },
      async login(body) {
        const user = await authApi.login(body)
        setState({ phase: 'in', user, notice: null })
      },
      async signup(body) {
        const user = await authApi.signup(body)
        setState({ phase: 'in', user, notice: null })
      },
      async logout() {
        // Logging out always ends locally, even if the server cannot be reached.
        await authApi.logout().catch(() => {})
        setState({ phase: 'out', user: null, notice: null })
      },
    }),
    [state, load],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
