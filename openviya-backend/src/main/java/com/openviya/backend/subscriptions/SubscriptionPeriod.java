package com.openviya.backend.subscriptions;

import java.time.Duration;

public enum SubscriptionPeriod {
    DAYS_7(7),
    DAYS_30(30),
    DAYS_90(90),
    DAYS_180(180),
    DAYS_365(365);

    private final Duration duration;

    SubscriptionPeriod(long days) {
        this.duration = Duration.ofDays(days);
    }

    public Duration duration() {
        return duration;
    }
}
