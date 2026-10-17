# Telegram user authentication

The browser login uses Telegram's OpenID Connect Authorization Code flow through Spring Security. Telegram OIDC is disabled by default. Configure the BotFather client ID and secret and register the backend callback URL before enabling it. Telegram's documentation specifies authorization code flow with PKCE and places `profile.id` separately from the OIDC `sub`; the backend relies on those distinct claims. See [Telegram Login](https://core.telegram.org/bots/telegram-login).

The token exchange and JWKS retrieval explicitly support gzip responses with a 1 MiB decompressed-body limit. Telegram was observed returning compressed key sets that the default JDK HTTP transport did not decode, causing `invalid_id_token` / `Malformed Jwk set`. The decoder retains RS256 verification, Spring's standard claim conversion, and the default validators plus `OidcIdTokenValidator`; state, nonce and PKCE remain handled by Spring Security. `TelegramOidcTransportTest` covers compressed/plain keys, compressed token responses, malformed/oversized gzip, and rejection of invalid signatures, issuer, audience and expiry.

The verified `id` claim accepts either an exact JSON number or a canonical decimal string, with the same positive 52-bit range. Strings with signs, whitespace, leading zeros, fractions or scientific notation are rejected. The OIDC `sub` is never substituted for missing `id`. Profile text limits count Unicode code points to match PostgreSQL `varchar`. Mapping failures log only an internal stage, an ID type category and a failure category, never claim values or tokens. `TelegramProfileTest` and `TelegramOidcSuccessHandlerTest` cover canonicalization, the authenticated user session, missing IDs and suspended accounts.

## Configuration

Set these environment variables:

- `OPENVIYA_TELEGRAM_OIDC_ENABLED=true`
- `OPENVIYA_TELEGRAM_OIDC_CLIENT_ID` and `OPENVIYA_TELEGRAM_OIDC_CLIENT_SECRET`
- `OPENVIYA_PUBLIC_BASE_URL` to the account page on the API host, for example `https://api.openviya.online/account/`.
- Optionally, `OPENVIYA_TELEGRAM_OIDC_REDIRECT_URI`; the callback stays on the API host, for example `https://api.openviya.online/login/oauth2/code/telegram`. The default callback is `{baseUrl}/login/oauth2/code/{registrationId}`.
- `OPENVIYA_SESSION_COOKIE_SECURE=true` in production.

Register the exact callback URL and the public website origin in Telegram's allowed URLs. Keep the client secret in the runtime secret store. The implementation requests only `openid profile`; it does not request a phone number or bot messaging permission.

The account page is served on the API origin. `SESSION` remains a host-only cookie (no `Domain` attribute), with `Path=/`, `HttpOnly`, `Secure` in production, and `SameSite=Lax`. The root path is needed because Spring stores the OIDC authorization request in the session on `/oauth2/authorization/telegram` and reads it on `/login/oauth2/code/telegram`; both paths are outside the API path. The cookie remains restricted to the API hostname because `Domain` is unset.

## Login flow

1. From the account page on the API origin, the browser navigates to `GET /api/v1/auth/telegram/login`.
2. When OIDC is configured, the server redirects to Telegram through `/oauth2/authorization/telegram`. It uses authorization code, `state`, `nonce`, and PKCE S256.
3. Spring Security validates the OIDC response and ID token, including signature, issuer, audience, expiry, state, and nonce. The callback is `GET /login/oauth2/code/telegram`.
4. The server maps the verified numeric `profile.id` claim to `external_identity.provider_subject`, creates or resolves the internal user, stores a server-side session, and redirects to `OPENVIYA_PUBLIC_BASE_URL` (the account page).
5. `GET /api/v1/me` returns the current user's internal ID and Telegram profile. Suspended/closed accounts cannot establish or keep an authenticated session.
6. `GET /api/v1/auth/csrf` returns `headerName` and `token`; send the token in the named header for state-changing requests. `POST /api/v1/auth/logout` invalidates the current server-side session and requires this CSRF token.

Unauthenticated requests to protected API routes return HTTP `401` with `{"code":"UNAUTHORIZED"}` instead of starting a browser login redirect. The login page starts OIDC explicitly through the login route above.

The OIDC `sub` is not treated as the Bot API numeric user ID. The provider's documented claims distinguish `sub` from the profile's numeric `id`; linking to an existing bot identity must use the latter. A live Bot API integration contract check remains required before marking that cross-channel mapping verified.

When OIDC is not configured, the login endpoint returns `503 Service Unavailable`. Telegram login does not accept a caller-supplied Telegram user ID.

## Trusted bot identity resolution

The separately deployed trusted bot resolves a Telegram identity through `POST /api/v1/internal/telegram/identity/resolve`. Set a random service token of at least 32 characters as `OPENVIYA_TELEGRAM_BOT_SERVICE_TOKEN` in both services. Send it as `Authorization: Bearer …` over TLS:

```json
{
  "telegramUserId": "123456789",
  "username": "example",
  "displayName": "Example User"
}
```

The endpoint returns only `{ "userId": "…" }`. It uses distributed PostgreSQL rate limits for source and Telegram ID, stores no raw service token, and writes a system audit event. Since it authenticates with an explicit Bearer header rather than a browser cookie, CSRF is exempted only for this exact route. Without the service token it returns `503`; invalid credentials return `401`. Only this exact internal route is allowed through the security matcher. The bot must authenticate Telegram updates with Telegram's own webhook mechanism before forwarding the ID; the backend does not accept an unauthenticated user ID as proof of identity.

Rotate the shared service token by changing it in both services and restarting them. Do not place it in browser code or client requests.
