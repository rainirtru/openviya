package com.openviya.backend.subscriptions;

import java.time.Duration;
import java.time.Instant;
import java.util.Objects;

public final class SubscriptionTime {

    public static final Duration TRIAL_DURATION = Duration.ofHours(24);
    public static final int TRIAL_DEVICE_LIMIT = 2;
    public static final int STANDARD_DEVICE_LIMIT = 5;

    private SubscriptionTime() {
    }

    public static Instant endAfter(Instant currentEnd, Instant activatedAt, Duration duration) {
        Objects.requireNonNull(activatedAt, "Activation time is required");
        Objects.requireNonNull(duration, "Subscription duration is required");
        if (duration.isZero() || duration.isNegative()) {
            throw new IllegalArgumentException("Subscription duration must be positive");
        }
        Instant start = currentEnd == null || currentEnd.isBefore(activatedAt) ? activatedAt : currentEnd;
        return start.plus(duration);
    }

    public static Instant trialEnd(Instant activatedAt) {
        return endAfter(null, activatedAt, TRIAL_DURATION);
    }
}
