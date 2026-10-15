# Subscription and trial API

User routes require an authenticated Telegram web session and retain CSRF protection.

## Claim trial access

`POST /api/v1/trials/claim` creates one durable trial claim for the authenticated Telegram identity. The backend verifies that the session's Telegram ID is still linked to the active internal user, creates that user's single subscription row if needed, and records a 24-hour/two-device pending grant and a durable `TRIAL_ACTIVATION` operation in the same database transaction.

The response is `202 Accepted` while activation is pending:

```json
{
  "operationId": "…",
  "state": "REQUESTED",
  "activatedAt": null
}
```

Repeated requests for the same Telegram identity return the existing claim; they do not create another grant. A different claim is rejected when the user already has a non-inactive subscription. A claim ID cannot be read by another user.

## Read trial activation

`GET /api/v1/access-operations/{operationId}` returns the current state of the caller's trial operation. It returns `404` for an unknown operation or one owned by another user.

The operation is persisted for recovery, but the worker that applies access to 3x-ui is part of Stage 5 and is not implemented yet. Until then a new claim remains `REQUESTED`; it does not grant VPN access. Trial eligibility checks against paid/gift orders will be extended when the order model is implemented in Stage 7.
