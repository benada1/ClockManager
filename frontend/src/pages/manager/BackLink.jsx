import { ChevronLeft } from '../../components/icons.jsx'

/** "‹ Team" style link back up one level. */
export default function BackLink({ href, children }) {
  return (
    <nav aria-label="Breadcrumb">
      <a href={href} className="inline-flex min-h-11 items-center gap-1.5 text-sm font-medium text-accent-ink no-underline wrap-anywhere hover:underline">
        <ChevronLeft size={16} />
        {children}
      </a>
    </nav>
  )
}
