import { useEffect } from 'react'

/** When `trigger` changes (a failed submit), focus the first field marked aria-invalid so the error is noticed. */
export default function useFocusInvalid(formRef, trigger) {
  useEffect(() => {
    formRef.current?.querySelector('[aria-invalid="true"]')?.focus()
  }, [formRef, trigger])
}
