package com.openviya.backend.subscriptions;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

public record SubscriptionSnapshot(
        UUID subscriptionId,
        String state,
        boolean accessActive,
        Instant endsAt,
        int deviceLimit,
        long version,
        boolean trialUsed,
        boolean standardAccessReceived,
        boolean paidPurchaseConfirmed,
        List<GrantSnapshot> grants) {

    public record GrantSnapshot(
            String sourceType,
            String state,
            long durationSeconds,
            Instant startsAt,
            Instant endsAt,
            Instant confirmedAt,
            Instant paidAt,
            Instant createdAt) {
    }
}
