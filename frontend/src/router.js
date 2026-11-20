import { useSyncExternalStore } from 'react'

// Minimal hash routing (#/history). The app has very few screens, so a router
// dependency would not pay for itself; this also needs no server-side fallback config.

function subscribe(onChange) {
  window.addEventListener('hashchange', onChange)
  return () => window.removeEventListener('hashchange', onChange)
}

const getPath = () => window.location.hash.replace(/^#/, '') || '/'

/** Current route path, e.g. "/" or "/history". */
export function useHashRoute() {
  return useSyncExternalStore(subscribe, getPath, () => '/')
}

export const href = (path) => `#${path}`

/** Go to a page without adding a history entry (used for redirects). */
export const navigate = (path) => window.location.replace(`#${path}`)
