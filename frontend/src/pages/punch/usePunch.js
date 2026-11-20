import { useEffect, useState, useSyncExternalStore } from 'react'
import { getStatus, punch } from '../../api/punches.js'
import newKey from '../../lib/newKey.js'
import { createPunchController } from './punchMachine.js'

/** Wires the punch controller to React. All rules live in punchMachine.js. */
export default function usePunch() {
  const [controller] = useState(() =>
    createPunchController({
      api: { getStatus, punch },
      newKey,
      now: () => performance.now(), // clock-ok: monotonic duration clock, marks when the server time arrived
    }),
  )
  const state = useSyncExternalStore(controller.subscribe, controller.getState)

  useEffect(() => {
    controller.refresh()
    // Another tab may have punched: look again on focus or network return (ignored while punching).
    const again = () => {
      if (document.visibilityState === 'visible') controller.refresh()
    }
    document.addEventListener('visibilitychange', again)
    window.addEventListener('focus', again)
    window.addEventListener('online', again)
    const resync = setInterval(again, 60000) // re-sync the shown server clock once a minute (D-084)
    return () => {
      clearInterval(resync)
      document.removeEventListener('visibilitychange', again)
      window.removeEventListener('focus', again)
      window.removeEventListener('online', again)
      controller.dispose()
    }
  }, [controller])

  return { ...state, press: controller.press, refresh: controller.refresh }
}
