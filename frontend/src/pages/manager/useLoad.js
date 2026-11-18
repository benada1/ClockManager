import { useEffect, useState, useSyncExternalStore } from 'react'
import { describeLoadFailure } from '../history/historyController.js'

/** Numbered requests (an older answer never wins); the data on screen is kept when a reload fails. */
export function createLoader(load) {
  let state = { data: null, loading: true, failure: null }
  let seq = 0
  let abort = null
  const listeners = new Set()
  const set = (patch) => {
    state = { ...state, ...patch }
    listeners.forEach((l) => l())
  }

  const reload = () => {
    const mine = ++seq
    abort?.abort()
    abort = new AbortController()
    set({ loading: true })
    return load(abort.signal)
      .then((data) => mine === seq && set({ data, loading: false, failure: null }))
      .catch((err) => {
        if (mine !== seq || err?.name === 'AbortError') return
        set({ loading: false, failure: describeLoadFailure(err) })
      })
  }

  return {
    subscribe: (l) => (listeners.add(l), () => listeners.delete(l)),
    getState: () => state,
    reload,
    // A fresh answer from elsewhere (the 201 of a correction) replaces the shown data.
    replace(data) {
      seq++
      abort?.abort()
      set({ data, loading: false, failure: null })
    },
    dispose() {
      seq++
      abort?.abort()
    },
  }
}

/** Loads on mount and when the window gets focus (others may have changed things). `load` is read once. */
export default function useLoad(load) {
  const [loader] = useState(() => createLoader(load))
  const state = useSyncExternalStore(loader.subscribe, loader.getState)

  useEffect(() => {
    loader.reload()
    window.addEventListener('focus', loader.reload)
    return () => {
      window.removeEventListener('focus', loader.reload)
      loader.dispose()
    }
  }, [loader])

  return { ...state, reload: loader.reload, replace: loader.replace }
}
