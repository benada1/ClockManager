// Small inline SVG icons (stroke = currentColor). Always decorative: the text next to them carries the meaning.
function Svg({ size = 18, strokeWidth = 2, children, fill = 'none' }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={fill}
      stroke={fill === 'none' ? 'currentColor' : 'none'}
      strokeWidth={strokeWidth}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      className="shrink-0"
    >
      {children}
    </svg>
  )
}

export const ClockIcon = (p) => (
  <Svg strokeWidth={2.2} {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7v5l3 2" />
  </Svg>
)

export const SunIcon = (p) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
  </Svg>
)

export const MoonIcon = (p) => (
  <Svg {...p}>
    <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
  </Svg>
)

export const PlayIcon = (p) => (
  <Svg size={20} strokeWidth={2.2} {...p}>
    <path d="M7 5l12 7-12 7z" />
  </Svg>
)

export const StopIcon = (p) => (
  <Svg fill="currentColor" {...p}>
    <rect x="6" y="6" width="12" height="12" rx="2" />
  </Svg>
)

export const PinIcon = (p) => (
  <Svg size={14} {...p}>
    <path d="M12 21s-7-6.1-7-11.5A7 7 0 0 1 19 9.5C19 14.9 12 21 12 21z" />
    <circle cx="12" cy="9.5" r="2.5" />
  </Svg>
)

export const ChevronLeft = (p) => (
  <Svg {...p}>
    <path d="M15 18l-6-6 6-6" />
  </Svg>
)

export const ChevronRight = (p) => (
  <Svg {...p}>
    <path d="M9 18l6-6-6-6" />
  </Svg>
)

export const CalendarIcon = (p) => (
  <Svg size={28} strokeWidth={1.8} {...p}>
    <rect x="3" y="4.5" width="18" height="16" rx="3" />
    <path d="M3 9.5h18M8 2.5v4M16 2.5v4" />
    <path d="M12 13v2.5l1.5 1" />
  </Svg>
)
