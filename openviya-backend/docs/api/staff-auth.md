# Staff authentication

All write requests, including unauthenticated login challenges and owner administration, require the CSRF token returned by `GET /api/v1/staff/auth/csrf`. Send it in the header named by `headerName` (currently `X-CSRF-TOKEN`) and retain the `OPENVIYA_SESSION` cookie.

## Owner staff administration

All routes below require an authenticated `OWNER` session and the CSRF header described above. Administrative requests require a non-empty `reason`; it is stored with the audit event. Passwords must be at least 16 characters and no more than 72 UTF-8 bytes. Supported roles are `OWNER`, `ADMIN`, and `SUPPORT`.

### Create a staff account

`POST /api/v1/admin/staff`

```json
{
  "login": "operator",
  "password": "a-long-one-time-password",
  "roles": ["SUPPORT"],
  "reason": "Support onboarding"
}
```

Creates a `PENDING` account. The staff member must complete TOTP enrollment on first login. Duplicate normalized logins return `409 STAFF_LOGIN_ALREADY_EXISTS`.

### Replace roles

`PUT /api/v1/admin/staff/{id}/roles`

```json
{
  "roles": ["ADMIN"],
  "reason": "Approved role change"
}
```

This replaces the complete role set and increments `auth_version`, invalidating existing sessions. Removing the last active owner's `OWNER` role returns `409 LAST_ACTIVE_OWNER_REQUIRED`.

### Change status

`PATCH /api/v1/admin/staff/{id}/status`

```json
{
  "status": "LOCKED",
  "reason": "Temporary access suspension"
}
```

Accepted statuses are `ACTIVE`, `LOCKED`, and `DISABLED`. Changing status increments `auth_version`. The last active owner cannot be locked or disabled.

## One-time owner recovery

For a lost owner password or second factor, set `OPENVIYA_OWNER_RECOVERY_ENABLED=true`, provide the existing owner's login and a new one-time password using `OPENVIYA_OWNER_RECOVERY_LOGIN` and `OPENVIYA_OWNER_RECOVERY_PASSWORD`, then start the application once. Recovery cannot run together with initial owner bootstrap, requires a matching active owner, and can only be used once per owner. It resets TOTP and recovery codes, sets the account to `PENDING`, and increments `auth_version` to revoke sessions. The next login must enroll TOTP again.

Immediately disable recovery and remove its credentials from the runtime environment after successful startup. A repeated recovery attempt fails closed. The action is written to the audit log as a system event; neither the password nor 2FA secrets are recorded.

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

Telegram user login and the corresponding external identity contract remain future API work.
