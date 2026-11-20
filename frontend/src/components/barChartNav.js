/** Column to focus for a key inside the chart (one tab stop, arrows move, D-088), or null for other keys. */
export function nextColumn(key, index, count) {
  if (key === 'ArrowRight' || key === 'ArrowDown') return Math.min(count - 1, index + 1)
  if (key === 'ArrowLeft' || key === 'ArrowUp') return Math.max(0, index - 1)
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  return null
}

export const TIP_START = { hover: null, focus: null, dismissed: false }

/**
 * Tooltip state. `focus` is the focused or clicked (pinned) day; Escape and a press outside release it, so a
 * dismissed card never comes back on its own. Any new hover, focus, click or key on a day shows it again.
 */
export function tipReducer(state, { type, index }) {
  switch (type) {
    case 'enter':
      return { ...state, hover: index, dismissed: false }
    case 'leave':
      return state.hover === index ? { ...state, hover: null } : state
    case 'focus':
    case 'click':
    case 'key':
      return { ...state, focus: index, dismissed: false }
    case 'blur':
      return state.focus === index ? { ...state, focus: null } : state
    case 'escape':
      return { ...state, focus: null, dismissed: true }
    case 'outside':
      return TIP_START
    default:
      return state
  }
}

/** The day whose card shows, or null. */
export const shownDay = (state) => (state.dismissed ? null : (state.hover ?? state.focus))

/** Tooltip anchor: the two outer columns on each side align to their edge so the card never leaves the chart. */
export function tipAlign(index, count) {
  if (index < 2) return 'start'
  if (index > count - 3) return 'end'
  return 'center'
}
