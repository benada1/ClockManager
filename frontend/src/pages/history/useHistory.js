import { useEffect, useState, useSyncExternalStore } from 'react'
import { getShifts } from '../../api/history.js'
import { createHistoryController } from './historyController.js'

/** Wires the history controller to React. The rules live in historyController.js. */
/** `load(range, signal)` defaults to my own shifts; the manager view passes an employee's loader (read once). */
export default function useHistory(load = getShifts) {
  const [controller] = useState(() => createHistoryController({ api: { getShifts: load } }))
  const state = useSyncExternalStore(controller.subscribe, controller.getState)

  useEffect(() => {
    controller.open()
    return controller.dispose
  }, [controller])

  return { ...state, previous: controller.previous, next: controller.next, retry: controller.retry }
}
