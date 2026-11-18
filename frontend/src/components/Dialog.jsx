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
      className="ui m-auto w-full max-w-xl rounded-ui border border-line bg-surface p-0 text-ink shadow-lg backdrop:bg-ink/40"
    >
      <div className="flex max-h-[90vh] flex-col gap-6 overflow-y-auto p-6">
        <div className="flex flex-col gap-1">
          <h2 id={titleId} className="text-xl font-semibold">
            {title}
          </h2>
          {description && <p className="text-base text-muted">{description}</p>}
        </div>
        {children}
      </div>
    </dialog>
  )
}
