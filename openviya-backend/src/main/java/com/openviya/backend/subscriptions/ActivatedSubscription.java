package com.openviya.backend.subscriptions;

import java.time.Instant;
import java.util.UUID;

public record ActivatedSubscription(UUID subscriptionId, UUID userId, Instant activatedAt, Instant endsAt) {
}
