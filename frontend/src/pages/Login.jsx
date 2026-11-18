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

export default function Login() {
  const { login, notice } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState(null)
  const [fields, setFields] = useState({})
  const formRef = useRef(null)
  useFocusInvalid(formRef, fields)

  async function submit(e) {
    e.preventDefault()
    setBusy(true)
    setProblem(null)
    setFields({})
    try {
      await login({ username: username.trim(), password })
    } catch (err) {
      // The typed values stay in the form; only the messages change.
      if (err instanceof ApiError && err.code === 'auth.invalid_credentials') {
        setProblem('That username or password is not right. Check them and try again.')
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
      title="Log in"
      footer={
        <>
          New here?{' '}
          <a className="font-medium text-accent underline" href={href('/signup')}>
            Create an account
          </a>
        </>
      }
    >
      <form ref={formRef} onSubmit={submit} noValidate className="flex flex-col gap-5">
        {notice && <Banner tone="warning" title={notice} />}
        {problem && <Banner tone="error" title={problem} />}
        <Field
          label="Username"
          name="username"
          autoComplete="username"
          autoCapitalize="none"
          autoCorrect="off"
          spellCheck={false}
          required
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          error={fields.username}
        />
        <Field
          label="Password"
          name="password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={fields.password}
        />
        <Button type="submit" busy={busy} busyLabel="Logging in…" block>
          Log in
        </Button>
      </form>
    </AuthLayout>
  )
}
