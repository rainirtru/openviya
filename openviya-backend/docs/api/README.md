# API contracts

The versioned application API uses `/api/v1`. Health endpoints are operational probes and are not part of the public business API.

Staff password/TOTP, owner-only staff management, and one-time owner recovery are documented in [staff-auth.md](staff-auth.md). Telegram OIDC login and trusted bot identity resolution are documented in [telegram-auth.md](telegram-auth.md). Other business API contracts will be added together with their application flows.

External control-center APIs are documented separately from the application API: [3x-ui adapter contract and known gaps](../integrations/3x-ui/README.md), based on the [provided OpenAPI snapshot](../integrations/3x-ui/openapi.json). The snapshot describes upstream endpoints; it is not an OpenViya public API or evidence of a completed integration.
