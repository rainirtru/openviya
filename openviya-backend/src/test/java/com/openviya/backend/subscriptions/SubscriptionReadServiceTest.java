package com.openviya.backend.subscriptions;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.when;

import java.sql.ResultSet;
import java.sql.Timestamp;
import java.time.Clock;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;

class SubscriptionReadServiceTest {

    private static final Instant NOW = Instant.parse("2026-10-15T10:00:00Z");
    private static final UUID USER_ID = UUID.fromString("87bcce83-3af2-4db1-b065-21ca3b681222");
    private static final UUID SUBSCRIPTION_ID = UUID.fromString("1b395f10-793c-48c2-ac3e-17609ddbaad3");

    @Test
    @SuppressWarnings({"rawtypes", "unchecked"})
    void reportsExpiredAtTheEndBoundaryAndKeepsTrialPromoAndPaidMarkersSeparate() throws Exception {
        JdbcTemplate jdbc = mock(JdbcTemplate.class);
        SubscriptionReadService service = new SubscriptionReadService(
                jdbc, Clock.fixed(NOW, ZoneOffset.UTC));

        ResultSet subscription = mock(ResultSet.class);
        when(subscription.getObject("id", UUID.class)).thenReturn(SUBSCRIPTION_ID);
        when(subscription.getString("state")).thenReturn("ACTIVE");
        when(subscription.getTimestamp("ends_at")).thenReturn(Timestamp.from(NOW));
        when(subscription.getInt("device_limit")).thenReturn(5);
        when(subscription.getLong("version")).thenReturn(2L);
        when(jdbc.query(anyString(), any(RowMapper.class), eq(USER_ID))).thenAnswer(invocation -> {
            RowMapper mapper = invocation.getArgument(1);
            return List.of(mapper.mapRow(subscription, 0));
        });

        ResultSet promotion = mock(ResultSet.class);
        when(promotion.getString("source_type")).thenReturn("PROMO");
        when(promotion.getString("state")).thenReturn("ACTIVE");
        when(promotion.getLong("duration_seconds")).thenReturn(7L * 24 * 60 * 60);
        when(promotion.getTimestamp("starts_at")).thenReturn(Timestamp.from(NOW.minusSeconds(60)));
        when(promotion.getTimestamp("ends_at")).thenReturn(Timestamp.from(NOW));
        when(promotion.getTimestamp("confirmed_at")).thenReturn(Timestamp.from(NOW.minusSeconds(120)));
        when(promotion.getTimestamp("paid_at")).thenReturn(null);
        when(promotion.getTimestamp("created_at")).thenReturn(Timestamp.from(NOW.minusSeconds(120)));
        when(jdbc.query(anyString(), any(RowMapper.class), eq(SUBSCRIPTION_ID))).thenAnswer(invocation -> {
            RowMapper mapper = invocation.getArgument(1);
            return List.of(mapper.mapRow(promotion, 0));
        });
        when(jdbc.queryForObject(anyString(), eq(Boolean.class), eq(USER_ID))).thenReturn(true);

        SubscriptionSnapshot result = service.getForUser(USER_ID);

        assertEquals("EXPIRED", result.state());
        assertFalse(result.accessActive());
        assertTrue(result.trialUsed());
        assertTrue(result.standardAccessReceived());
        assertFalse(result.paidPurchaseConfirmed());
        assertEquals(SUBSCRIPTION_ID, result.subscriptionId());
    }
}
