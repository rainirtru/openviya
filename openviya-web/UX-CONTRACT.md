# OpenViya Web UX Contract

This contract describes the Russian public site and the API-hosted signed-in account. Backend API behavior is authoritative; the frontend does not synthesize account or payment state.

## Routes and origins

- Public brand site: `https://openviya.online/`.
- Account: `https://api.openviya.online/account/`. The public host may redirect `/account/` to this route.
- Telegram OIDC entry: full-page navigation to `https://api.openviya.online/api/v1/auth/telegram/login`.
- The account and API share the API origin, so browser requests use relative `/api/v1/...` paths and same-origin credentials. The public marketing host does not make API requests.
- Page titles are localized and route-specific: `OpenViya — ваше личное пространство, где угодно` on `/` and `Личный кабинет — OpenViya` for the account, with a dedicated title when authentication is required.

## Authentication and session

- Telegram OIDC is a full-page redirect. Never ask the browser to collect or submit a Telegram ID.
- Login completion must redirect to `https://api.openviya.online/account/` (`OPENVIYA_PUBLIC_BASE_URL` in the backend deployment). The session cookie must be host-only on `api.openviya.online`, `HttpOnly`, `Secure` in production, `SameSite=Lax`, and use `Path=/` so Spring's OAuth callback and account API share it.
- The account initially reads `GET /api/v1/me` and `GET /api/v1/subscription` in parallel. On `401`, it shows an explicit Telegram sign-in link and hides protected account data. It does not infer authentication from the URL or browser storage.
- Refresh re-reads server state. Requests use `cache: no-store`, a 12-second timeout, and safe text rendering. Network/server errors remain inline and can be retried.
- Logout obtains a CSRF token from `GET /api/v1/auth/csrf` with `{ "headerName": "…", "token": "…"}`, then sends that token in the named header on `POST /api/v1/auth/logout`. On `204`, return to `https://openviya.online/`.
- Refresh is disabled while logout is pending. A stale account refresh must not re-render protected data after logout starts or the session expires.

## Account data

- `GET /api/v1/me` is the sole source for internal account ID and Telegram profile. Show display name, username when present, and Telegram ID; do not request phone or bot messaging permissions.
- `GET /api/v1/subscription` is the sole source for the subscription snapshot and grant history. Supported subscription values in the current model: `INACTIVE`, `PENDING`, `ACTIVE`, `EXPIRED`, `SUSPENDED`. `accessActive` is shown as a commercial subscription state; it does not prove that a VPN session is connected or disconnected.
- `deviceLimit`, `trialUsed`, and `paidPurchaseConfirmed` are displayed only as returned. Device limit is a configured limit, not a current device count.
- Grant rows use `sourceType`, `state`, `durationSeconds`, `startsAt`, `endsAt`, `confirmedAt`, `paidAt`, and `createdAt`. Source names are `TRIAL`, `PURCHASE`, and `PROMO`; stored grant states include `PENDING`, `ACTIVE`, `REVOKED`, and `SUPERSEDED`. Unknown future enum values remain generic and safe.
- Empty grants are represented by the absence of history, not sample entries. Do not invent prices, offers, invoices, payment status, VPN nodes/locations, connected devices, or connection telemetry.
- Trial claim is not offered in this UI while the activation worker is unavailable. `trialUsed` is read-only. Add a claim flow only after backend activation is implemented and its end-to-end result states are contractually available.

## Public copy and navigation

- User-facing copy, metadata, accessibility labels, and account status text avoid explicit VPN terminology. Describe OpenViya, internet access, the account, and subscription facts in plain language. This wording choice does not authorize stronger privacy, speed, availability, or other claims.
- The user-selected visual and slogan reference is `https://openviya.rainirtru.chatgpt.site/`. User-provided screenshots establish its slogan and visual direction. Use «Ваше личное пространство — где угодно» as the main slogan.
- Public copy describes the existing product at a general level and provides only account and Telegram login actions.
- No published plan, price, payment, speed, geography, availability, uptime, privacy guarantee, or support promise may be added without an approved product source.
- The route illustration is explanatory artwork and must not be described as a live network status.

## Feedback, accessibility, and responsive behavior

- Use native links for navigation and buttons for in-place actions. All controls have a visible keyboard focus ring and text labels.
- Loading, empty, unauthenticated, partial error, timeout, and server error are separate states. Errors do not expose raw exception text or payloads.
- Account requests are bounded; the user can explicitly retry. No automatic retries, local caching, or persistence of profile/subscription data are used.
- Public and account layouts reflow to one column on narrow screens. The document owns vertical scrolling. Scrollbars stay visible and themed; reduced-motion and forced-colors preferences are respected.
- Russian is the only locale in this module. Dates use `ru-RU` formatting; content remains sentence case and wraps rather than hiding important values.
