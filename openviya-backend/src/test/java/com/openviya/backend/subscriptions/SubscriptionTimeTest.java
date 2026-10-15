package com.openviya.backend.subscriptions;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.time.Duration;
import java.time.Instant;
import java.util.List;
import org.junit.jupiter.api.Test;

class SubscriptionTimeTest {

    private static final Instant NOW = Instant.parse("2026-10-15T10:00:00Z");

    @Test
    void exposesOnlySupportedCommercialPeriods() {
        assertEquals(List.of(7L, 30L, 90L, 180L, 365L),
                java.util.Arrays.stream(SubscriptionPeriod.values())
                        .map(period -> period.duration().toDays())
                        .toList());
    }

    @Test
    void trialIsExactlyTwentyFourHours() {
        assertEquals(Duration.ofHours(24), SubscriptionTime.TRIAL_DURATION);
        assertEquals(NOW.plus(Duration.ofHours(24)), SubscriptionTime.trialEnd(NOW));
    }

    @Test
    void extensionStartsAfterAnActivePeriod() {
        Instant currentEnd = NOW.plus(Duration.ofHours(10));
        assertEquals(currentEnd.plus(Duration.ofDays(30)),
                SubscriptionTime.endAfter(currentEnd, NOW, SubscriptionPeriod.DAYS_30.duration()));
    }

    @Test
    void renewalAfterExpiryDoesNotCountTheWaitingTime() {
        Instant expiredAt = NOW.minus(Duration.ofDays(5));
        assertEquals(NOW.plus(Duration.ofDays(7)),
                SubscriptionTime.endAfter(expiredAt, NOW, SubscriptionPeriod.DAYS_7.duration()));
    }

    @Test
    void rejectsZeroAndNegativePeriods() {
        assertThrows(IllegalArgumentException.class,
                () -> SubscriptionTime.endAfter(null, NOW, Duration.ZERO));
        assertThrows(IllegalArgumentException.class,
                () -> SubscriptionTime.endAfter(null, NOW, Duration.ofSeconds(-1)));
    }
}
