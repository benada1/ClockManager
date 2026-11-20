import { useRef, useState } from 'react'
import useFocusInvalid from '../lib/useFocusInvalid.js'
import { useAuth } from '../auth/authContext.js'
import { ApiError } from '../api/client.js'
import AuthLayout from '../components/AuthLayout.jsx'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Field from '../components/Field.jsx'
import { fieldErrors, generalMessage } from '../lib/errors.js'
import { href } from '../router.js'

export default function Signup() {
  const { signup } = useAuth()
  const [form, setForm] = useState({ username: '', displayName: '', password: '' })
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState(null)
  const [fields, setFields] = useState({})
  const formRef = useRef(null)
  useFocusInvalid(formRef, fields)
  const set = (name) => (e) => setForm((f) => ({ ...f, [name]: e.target.value }))

  async function submit(e) {
    e.preventDefault()
    setBusy(true)
    setProblem(null)
    setFields({})
    try {
      await signup({ ...form, username: form.username.trim(), displayName: form.displayName.trim() })
    } catch (err) {
      // The typed values stay in the form; only the messages change.
      if (err instanceof ApiError && err.code === 'auth.username_taken') {
        setFields({ username: 'This username is already taken. Try another one.' })
      } else if (err instanceof ApiError && err.status === 400) {
        const found = fieldErrors(err)
        setFields(found)
        if (Object.keys(found).length === 0) setProblem('Please check what you typed and try again.')
      } else {
        setProblem(generalMessage(err))
      }
      setBusy(false)
    }
  }

  return (
    <AuthLayout
      title="Create your account"
      intro="You will be logged in right away."
      footer={
        <>
          Already have an account?{' '}
          <a className="font-semibold text-accent-ink no-underline hover:underline" href={href('/login')}>
            Log in
          </a>
        </>
      }
    >
      <form ref={formRef} onSubmit={submit} noValidate className="flex flex-col gap-5">
        {problem && <Banner tone="error" title={problem} />}
        <Field
          label="Username"
          name="username"
          autoComplete="username"
          autoCapitalize="none"
          autoCorrect="off"
          spellCheck={false}
          required
          hint="3 to 32 characters: letters, digits, dot, dash or underscore."
          value={form.username}
          onChange={set('username')}
          error={fields.username}
        />
        <Field
          label="Your name"
          name="displayName"
          autoComplete="name"
          required
          hint="This is the name shown in the app."
          value={form.displayName}
          onChange={set('displayName')}
          error={fields.displayName}
        />
        <Field
          label="Password"
          name="password"
          type="password"
          autoComplete="new-password"
          required
          hint="At least 8 characters."
          value={form.password}
          onChange={set('password')}
          error={fields.password}
        />
        <Button type="submit" size="lg" busy={busy} busyLabel="Creating account…" block className="mt-1 hover:shadow-cta">
          Create account
        </Button>
      </form>
    </AuthLayout>
  )
}
