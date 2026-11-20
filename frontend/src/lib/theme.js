import { useSyncExternalStore } from 'react'

// Light/dark theme. Light is the default on a first visit (D-082); a choice is kept in localStorage.
// index.html sets the attribute before the bundle runs, with the same key and rule, so there is no flash.
export const THEME_KEY = 'cm-theme'

/** 'dark' only when exactly 'dark' was stored; anything else (missing, garbage, blocked storage) is 'light'. */
export function storedTheme(storage) {
  try {
    return storage?.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light'
  } catch {
    return 'light'
  }
}

/** Returns false when the browser refuses to store it (private mode, blocked storage). */
export function saveTheme(storage, theme) {
  try {
    storage.setItem(THEME_KEY, theme)
    return true
  } catch {
    return false
  }
}

const listeners = new Set()
const root = () => document.documentElement

function browserStorage() {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

export const currentTheme = () => (root().dataset.theme === 'dark' ? 'dark' : 'light')

export function setTheme(theme) {
  root().dataset.theme = theme
  saveTheme(browserStorage(), theme)
  listeners.forEach((l) => l())
}

function subscribe(listener) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useTheme() {
  return useSyncExternalStore(subscribe, currentTheme, () => 'light')
}
