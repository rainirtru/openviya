package com.openviya.backend.subscriptions;

import java.time.Instant;
import java.util.UUID;

public record ActivatedGrant(UUID grantId, UUID subscriptionId, Instant startsAt, Instant endsAt) {
}
