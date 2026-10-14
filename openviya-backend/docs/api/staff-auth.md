# Staff authentication

All write requests, including unauthenticated login challenges, require the CSRF token returned by `GET /api/v1/staff/auth/csrf`. Send the returned token in the header named by `headerName` (currently `X-CSRF-TOKEN`) and retain the `OPENVIYA_SESSION` cookie.

## Initial owner

The first owner is created only by the one-time deployment bootstrap. The account starts in `PENDING`; its password cannot grant administrative access before TOTP enrollment is confirmed. Disable the bootstrap setting and remove its login/password from the environment immediately after the first startup.

## Password and TOTP flow

1. `GET /api/v1/staff/auth/csrf` returns `{ "headerName": "X-CSRF-TOKEN", "token": "…" }` and creates a CSRF session.
2. `POST /api/v1/staff/auth/login` accepts `{ "login": "…", "password": "…" }`. It returns an opaque `challengeId`, `totpEnrollmentRequired`, and `expiresInSeconds`. The password challenge lasts five minutes by default and can be configured up to ten minutes.
3. For an unconfigured account, `POST /api/v1/staff/auth/totp/enrollment` accepts `{ "challengeId": "…" }` and returns a manual key plus an `otpauth:` URI. A repeated enrollment request returns the same pending secret.
4. `POST /api/v1/staff/auth/totp/verify` accepts `{ "challengeId": "…", "code": "…" }`. During enrollment it confirms the authenticator; for a configured account it completes login with TOTP or one recovery code.
5. Success returns the staff ID, login, current roles, and recovery codes only during initial enrollment. The recovery codes must be stored outside this service.
6. `POST /api/v1/staff/auth/logout` invalidates the current session.

Password login is limited to 10 attempts per account and 30 per source address per 15 minutes. TOTP/enrollment is limited to 5 per account and 15 per source address per 5 minutes. A rate-limit block lasts 15 minutes. Counters are shared through PostgreSQL and bucket keys are hashed. TOTP allows the adjacent 30-second windows and records the accepted step to reject replay.

Sessions are stored in PostgreSQL, expire after 30 minutes by default, use an HttpOnly `OPENVIYA_SESSION` cookie scoped to `/api/v1`, and are rechecked against current staff status, role set, and `auth_version` on requests. Production must use HTTPS and set `OPENVIYA_SESSION_COOKIE_SECURE=true`.

This contract covers staff login only. Telegram user login, employee management, role assignment, and owner recovery remain future API work.
