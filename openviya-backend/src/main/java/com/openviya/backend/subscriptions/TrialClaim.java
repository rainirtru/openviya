package com.openviya.backend.subscriptions;

import java.time.Instant;
import java.util.UUID;

public record TrialClaim(
        UUID id,
        UUID userId,
        UUID subscriptionId,
        TrialClaimState state,
        Instant activatedAt,
        Instant createdAt) {
}
