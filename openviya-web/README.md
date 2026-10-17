# OpenViya Web

Static Russian public site and account UI. There is no package manager, framework runtime, or external asset request.

## Static artifact

The deployable tree is `public/` only:

```text
public/
  index.html
  account/index.html
  assets/site.css
  assets/account.js
```

Package the contents of `public/` at the static site's document root. The same files are served on both hosts. The public root serves `/`; the API host serves `/account/`. Both hosts serve `/assets/*`. Direct navigation to `/account/` on the public root should redirect to `https://api.openviya.online/account/`.

## API contract used

Account requests are relative same-origin API calls from `api.openviya.online`:

- `GET /api/v1/me`
- `GET /api/v1/subscription`
- `GET /api/v1/auth/csrf` → `{ "headerName": "…", "token": "…" }` (implemented for browser logout)
- `POST /api/v1/auth/logout` with the returned token in the named header

Login is a full-page navigation to `https://api.openviya.online/api/v1/auth/telegram/login`. The public marketing host makes no cross-origin API calls. No CORS setup is needed for this flow.

The account does not call trial claim, purchase, payment, order, device, or VPN node endpoints. The current backend does not implement purchase/payment APIs or the worker that activates trial grants.

## Backend deployment requirements

- Enable Telegram OIDC and configure the Telegram client credentials and registered callback as described by `openviya-backend/docs/api/telegram-auth.md`.
- Set `OPENVIYA_PUBLIC_BASE_URL=https://api.openviya.online/account/` so successful OIDC login returns directly to the account on the cookie-owning host.
- Use a host-only session cookie on `api.openviya.online` with `HttpOnly`, `Secure`, `SameSite=Lax`, and `Path=/`; the OAuth callback path and API path must receive the same session.
- Keep the user CSRF read endpoint available alongside the CSRF-protected logout route.
- Route `/api/v1/*`, `/oauth2/*`, and `/login/oauth2/*` to the backend on the API host. Serve this `public/` tree on the same host for `/account/` and `/assets/*`.

See [DESIGN.md](DESIGN.md) for runtime style tokens and [UX-CONTRACT.md](UX-CONTRACT.md) for state, auth, and data-display behavior.
