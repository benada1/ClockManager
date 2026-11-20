/** Column to focus for a key inside the chart (one tab stop, arrows move, D-088), or null for other keys. */
export function nextColumn(key, index, count) {
  if (key === 'ArrowRight' || key === 'ArrowDown') return Math.min(count - 1, index + 1)
  if (key === 'ArrowLeft' || key === 'ArrowUp') return Math.max(0, index - 1)
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  return null
}

/** Tooltip anchor: the two outer columns on each side align to their edge so the card never leaves the chart. */
export function tipAlign(index, count) {
  if (index < 2) return 'start'
  if (index > count - 3) return 'end'
  return 'center'
}
