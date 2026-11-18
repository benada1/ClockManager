const TONES = {
  info: 'border-line-strong bg-canvas text-ink',
  warning: 'border-warn-line bg-warn-soft text-warn',
}

/** A small word label. Renders an <li> (inside a list) or a <span>. */
export default function Pill({ tone = 'info', as: Tag = 'li', children }) {
  return <Tag className={`inline-block rounded-ui border px-2 py-0.5 text-sm whitespace-nowrap ${TONES[tone]}`}>{children}</Tag>
}
