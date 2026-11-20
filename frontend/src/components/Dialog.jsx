import { useEffect, useId, useRef } from 'react'

/**
 * Modal built on the native <dialog>: the browser traps focus and makes the page behind inert.
 * Mount it to open it. Escape calls onClose (unless `locked`), and focus goes back to the opener on close.
 */
export default function Dialog({ title, description, onClose, locked = false, children }) {
  const ref = useRef(null)
  const titleId = useId()

  useEffect(() => {
    const dialog = ref.current
    const opener = document.activeElement
    if (!dialog.open) dialog.showModal()
    return () => {
      if (dialog.open) dialog.close()
      if (opener instanceof HTMLElement && opener.isConnected) opener.focus()
    }
  }, [])

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onCancel={(e) => {
        e.preventDefault() // React decides when it closes
        if (!locked) onClose()
      }}
      className="m-auto w-[calc(100%-2rem)] max-w-xl rounded-card border border-line bg-surface p-0 font-sans text-ink shadow-card backdrop:bg-backdrop"
    >
      <div className="flex max-h-[90vh] flex-col gap-6 overflow-y-auto p-6 sm:p-8">
        <div className="flex flex-col gap-1">
          <h2 id={titleId} className="text-xl font-semibold tracking-tight">
            {title}
          </h2>
          {description && <p className="text-sm text-muted wrap-anywhere">{description}</p>}
        </div>
        {children}
      </div>
    </dialog>
  )
}
