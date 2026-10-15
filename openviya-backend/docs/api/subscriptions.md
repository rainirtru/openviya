# Subscription and trial API

User routes require an authenticated Telegram web session and retain CSRF protection.

## Read subscription state and grant history

`GET /api/v1/subscription` returns only the authenticated user's subscription and accrual history. A user with no subscription receives `INACTIVE` with a null `subscriptionId`. The response separates whether the user has used a trial, received standard access, or had a paid purchase confirmed; a free coupon or gift does not count as a paid purchase.

`state` reports `EXPIRED` as soon as `endsAt` is reached, even if the persisted state has not yet been reconciled by the Stage 5 access worker. `accessActive` is false at that boundary. This endpoint reports commercial state; it does not prove that an external VPN node has already disconnected a session.

Each grant reports its source, duration, confirmation/payment times, activation interval, and current state. No provider IDs, payment details, subscription URL, or device identifiers are returned.

```json
{
  "subscriptionId": "…",
  "state": "ACTIVE",
  "accessActive": true,
  "endsAt": "2026-11-14T10:00:00Z",
  "deviceLimit": 5,
  "version": 2,
  "trialUsed": true,
  "standardAccessReceived": true,
  "paidPurchaseConfirmed": false,
  "grants": []
}
```

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

Commercial `PURCHASE` and `PROMO` grants use the same durable grant history. Commercial confirmation (and, separately, successful payment) is recorded before activation; activation is idempotent, extends from the later of the current end and activation confirmation, and changes the normal device limit to five while preserving the subscription identity. The order and gift workflows that create these grants are part of Stage 7. A conflicting pending access change is rejected instead of guessing the unresolved trial-versus-purchase policy from technical-plan section 22.4.
