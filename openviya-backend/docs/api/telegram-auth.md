# Telegram user authentication

The browser login uses Telegram's OpenID Connect Authorization Code flow through Spring Security. Telegram OIDC is disabled by default. Configure the BotFather client ID and secret and register the backend callback URL before enabling it. Telegram's documentation specifies authorization code flow with PKCE and places `profile.id` separately from the OIDC `sub`; the backend relies on those distinct claims. See [Telegram Login](https://core.telegram.org/bots/telegram-login).

## Configuration

Set these environment variables:

- `OPENVIYA_TELEGRAM_OIDC_ENABLED=true`
- `OPENVIYA_TELEGRAM_OIDC_CLIENT_ID` and `OPENVIYA_TELEGRAM_OIDC_CLIENT_SECRET`
- Optionally, `OPENVIYA_TELEGRAM_OIDC_REDIRECT_URI`; the default callback is `{baseUrl}/login/oauth2/code/telegram`.

Register the exact callback URL and the frontend origin in Telegram's allowed URLs. Keep the client secret in the runtime secret store. The implementation requests only `openid profile`; it does not request a phone number or bot messaging permission.

## Login flow

1. The browser navigates to `GET /api/v1/auth/telegram/login`.
2. When OIDC is configured, the server redirects to Telegram through `/oauth2/authorization/telegram`. It uses authorization code, `state`, `nonce`, and PKCE S256.
3. Spring Security validates the OIDC response and ID token, including signature, issuer, audience, expiry, state, and nonce. The callback is `GET /login/oauth2/code/telegram`.
4. The server maps the verified numeric `profile.id` claim to `external_identity.provider_subject`, creates or resolves the internal user, stores a server-side session, and redirects to `openviya.public-base-url`.
5. `GET /api/v1/me` returns the current user's internal ID and Telegram profile. Suspended/closed accounts cannot establish or keep an authenticated session.
6. `POST /api/v1/auth/logout` invalidates the current server-side session and requires the CSRF token.

The OIDC `sub` is not treated as the Bot API numeric user ID. The provider's documented claims distinguish `sub` from the profile's numeric `id`; linking to an existing bot identity must use the latter. A live Bot API integration contract check remains required before marking that cross-channel mapping verified.

When OIDC is not configured, the login endpoint returns `503 Service Unavailable`. Telegram login does not accept a caller-supplied Telegram user ID.
