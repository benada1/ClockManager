import { setTheme, useTheme } from '../lib/theme.js'
import { MoonIcon, SunIcon } from './icons.jsx'

/** Sun in dark mode, moon in light mode. The label says what a press does (no aria-pressed: the label already changes). */
export default function ThemeToggle() {
  const theme = useTheme()
  const next = theme === 'dark' ? 'light' : 'dark'
  const label = `Switch to ${next} mode`
  return (
    <button
      type="button"
      onClick={() => setTheme(next)}
      aria-label={label}
      title={label}
      className="inline-flex size-11 items-center justify-center rounded-control border border-control bg-surface text-ink-2 transition-colors hover:bg-subtle"
    >
      {theme === 'dark' ? <SunIcon /> : <MoonIcon />}
    </button>
  )
}
