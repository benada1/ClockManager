# 04 — Auth (signup, login, JWT in an HTTP-only cookie)

**Status:** Approved 2026-11-18 · Steps 1–2 done (signup, login, logout, me, cookie JWT, dev header removed, D-049..D-053) · Step 3 (rate limits, seed manager password, README) next · **Lead agent:** backend
**Refs:** master plan D10 (changed: JWT in a cookie, by the user's choice) · D-027 (dev header) · CLAUDE.md rules 2 and 3

## 1. Goal
People can **sign up**, **log in** and **log out**. The login is a **JWT kept in an HTTP-only cookie**, so browser scripts can't read it.
The punch and status endpoints then know who is calling, and the temporary `X-Dev-Employee` header is removed.
`PunchService` does not change: only the `ICurrentEmployee` behind it does (D-027).

## 2. Scope
**In:** signup, login, logout, "who am I", password hashing, JWT issue + validation, role on the token, rate limits on login/signup,
removing the dev header, a migration for the password hash, tests.
**Out:** password reset, e-mail verification, refresh tokens, "remember me", manager screens (Phase 6/7).

## 3. API contract (the frontend builds against this)
All bodies are JSON. Errors are ProblemDetails with a stable `code`, as everywhere else.

| Endpoint | Body | Success | Errors |
|---|---|---|---|
| `POST /api/auth/signup` | `{ username, displayName, password }` | **201** `{ username, displayName, role }` and the cookie is set (signed in right away) | 400 `request.invalid` (field messages) · 409 `auth.username_taken` · 429 |
| `POST /api/auth/login` | `{ username, password }` | **200** `{ username, displayName, role }` + cookie | 401 `auth.invalid_credentials` (same answer for a wrong user or password) · 429 |
| `POST /api/auth/logout` | none | **204**, cookie cleared | none (always 204) |
| `GET /api/auth/me` | none | **200** `{ username, displayName, role }` | 401 `auth.unauthenticated` |

- `POST /api/punches` and `GET /api/me/status` need the cookie: no cookie, a bad or an expired token gives **401 `auth.unauthenticated`**.
  A deactivated employee gets 403 `employee.inactive` (as today).
- Signup always creates the role **Employee**. A **Manager** exists only through the seed (see §5).
- Rules: username 3–32 characters (letters, digits, `.`, `_`, `-`), display name 1–128, password 8–128. Username is unique, case-insensitive.

## 4. Design
- **Token:** HS256 JWT with `sub` (employee id), `role` and `exp`. Lifetime **8 hours**, no refresh: after that the user logs in again.
- **Cookie:** `HttpOnly`, `SameSite=Lax`, path `/`, `Secure` whenever the request is HTTPS (not on plain `http://localhost`).
  The Vite proxy makes the API same-origin for the browser, so no CORS and no CSRF token are needed. Lax blocks cross-site POSTs,
  and the API only accepts `application/json` bodies. Recorded as a decision.
- **Signing key:** from user-secrets or an environment variable (rule 3: not in git). In Development, if none is set, a random key is
  made at startup (logins end when the server restarts). Any other environment refuses to start without a key.
- **Passwords:** `PasswordHasher<Employee>` (PBKDF2). New column `PasswordHash` on `Employees` (nullable: seeded users have none and can't log in
  until they get one). A migration adds it. Login does the same work for an unknown user, so timing doesn't reveal which names exist.
- **Time:** the token's issue and expiry time use the **server clock**. That is allowed: it is not a punch record (rule 1). It goes through one small
  class with a `clock-ok` comment, and the decision is logged. Punch times still come only from the trusted clock.
- **`ICurrentEmployee`:** reads the validated cookie principal, loads the employee, and keeps today's 401/403 behavior. The dev-header class and its Swagger
  filter are deleted, and the tests that used the header switch to a test login helper.
- **Rate limiting:** built-in .NET rate limiter: login and signup per IP (e.g. 10 per minute), answer 429 `request.rate_limited`. Punches get a generous per-user limit.
- **Seed (Development only):** the manager from the seed gets a password only if `Seed:ManagerPassword` is set (user-secrets). Otherwise seeded users can't log in,
  and you use signup. No password is stored in git.

## 5. Steps and commits (each: tests + verify + CI before the next)
| # | Task | Commit subject (short) |
|---|---|---|
| 1 | Migration + password hasher + signup/login/logout/me + JWT cookie + tests | `feat(auth): sign up, log in and out with a cookie` |
| 2 | `ICurrentEmployee` from the cookie, remove the dev header, move tests to the login helper | `feat(auth): punches use the logged-in user` |
| 3 | Rate limits, seed manager password, README and decisions | `feat(auth): rate limits and seed manager` |
| 4 | Quick qa pass: only realistic cases (wrong password, expired token, tampered cookie, other user's data, rate limit) | `test(auth): login and token checks` |

## 6. Tests (main cases only)
Signup (valid, taken name, weak password, bad name) · login (right, wrong password, unknown user: same 401) · cookie flags (`HttpOnly`, `SameSite`) ·
`me` with and without cookie · expired and tampered token → 401 · punch without login → 401 · two users never see each other's shifts ·
Production ignores the old dev header · rate limit → 429 · password never appears in a response or a log.

## 7. Risks
| Risk | Mitigation |
|---|---|
| JWT can't be revoked before it expires | Short 8 h life; deactivated employees are still blocked because the employee is loaded on each request. Logged as a known limit. |
| Cookie not sent through the Vite proxy | Same-origin proxy (already used for `/api`); the frontend plan tests the full login in the browser. |
| Server clock for token expiry vs rule 1 | Isolated in one class, clearly marked, never used for punches. |

## 8. Questions for you (I will use the recommended answer unless you say otherwise)
- **Q1.** Token life **8 hours** with no refresh? *(recommended: yes, simple)*
- **Q2.** Signup is **open** to anyone and only creates Employees; Managers come from the seed? *(recommended: yes)*
- **Q3.** Seeded users (alice, bob, carol, manager) **can't log in** until a password is set; you create your own users by signup? *(recommended: yes, no password in git)*
