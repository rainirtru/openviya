package com.openviya.backend.subscriptions;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

import java.sql.ResultSet;
import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;

class SubscriptionActivationServiceTest {

    private static final Instant ACTIVATED_AT = Instant.parse("2026-10-15T10:00:00Z");
    private static final UUID GRANT_ID = UUID.fromString("7c34d744-28b0-4d4d-ae41-e64bfa01e0d1");
    private static final UUID SUBSCRIPTION_ID = UUID.fromString("1b395f10-793c-48c2-ac3e-17609ddbaad3");
    private static final UUID USER_ID = UUID.fromString("87bcce83-3af2-4db1-b065-21ca3b681222");

    private JdbcTemplate jdbc;
    private SubscriptionActivationService activations;

    @BeforeEach
    void setUp() {
        jdbc = mock(JdbcTemplate.class);
        activations = new SubscriptionActivationService(jdbc);
    }

    @Test
    void extendsAnUnexpiredSubscriptionWithoutLosingItsRemainingTime() throws Exception {
        Instant currentEnd = ACTIVATED_AT.plus(Duration.ofHours(10));
        stubGrant("PURCHASE", "PENDING", "ACTIVE", currentEnd, null, ACTIVATED_AT);
        when(jdbc.update(anyString(), any(Object[].class))).thenReturn(1);

        ActivatedGrant result = activations.activateCommercialGrant(GRANT_ID, ACTIVATED_AT);

        assertEquals(currentEnd, result.startsAt());
        assertEquals(currentEnd.plus(Duration.ofDays(30)), result.endsAt());
        assertEquals(SUBSCRIPTION_ID, result.subscriptionId());
    }

    @Test
    void renewalAfterExpiryStartsAtActivationInsteadOfCountingWaitingTime() throws Exception {
        stubGrant("PURCHASE", "PENDING", "EXPIRED", ACTIVATED_AT.minus(Duration.ofDays(5)), null, ACTIVATED_AT);
        when(jdbc.update(anyString(), any(Object[].class))).thenReturn(1);

        ActivatedGrant result = activations.activateCommercialGrant(GRANT_ID, ACTIVATED_AT);

        assertEquals(ACTIVATED_AT, result.startsAt());
        assertEquals(ACTIVATED_AT.plus(Duration.ofDays(30)), result.endsAt());
    }

    @Test
    void refusesActivationBeforeCommercialConfirmation() throws Exception {
        stubGrant("PURCHASE", "PENDING", "ACTIVE", ACTIVATED_AT.plus(Duration.ofDays(2)), null, null);

        SubscriptionActivationException exception = assertThrows(
                SubscriptionActivationException.class,
                () -> activations.activateCommercialGrant(GRANT_ID, ACTIVATED_AT));

        assertEquals("SUBSCRIPTION_GRANT_NOT_CONFIRMED", exception.getMessage());
        verify(jdbc, never()).update(anyString(), any(Object[].class));
    }

    @Test
    void repeatedActivationReturnsTheOriginalGrantIntervalWithoutUpdatingAgain() throws Exception {
        Instant start = ACTIVATED_AT.minus(Duration.ofDays(1));
        Instant end = start.plus(Duration.ofDays(30));
        stubGrant("PURCHASE", "ACTIVE", "ACTIVE", end, start, ACTIVATED_AT);

        ActivatedGrant result = activations.activateCommercialGrant(GRANT_ID, ACTIVATED_AT.plus(Duration.ofDays(20)));

        assertEquals(start, result.startsAt());
        assertEquals(end, result.endsAt());
        verify(jdbc, never()).update(anyString(), any(Object[].class));
    }

    @Test
    @SuppressWarnings({"rawtypes", "unchecked"})
    void repeatedTrialActivationReturnsTheSavedEndWithoutAddingAnotherDay() throws Exception {
        UUID claimId = UUID.fromString("48d4ae50-50ab-4380-b220-f841fa9a2ae0");
        Instant originalActivation = ACTIVATED_AT.minus(Duration.ofHours(20));
        Instant originalEnd = originalActivation.plus(Duration.ofHours(24));
        ResultSet rs = mock(ResultSet.class);
        when(rs.getObject("user_id", UUID.class)).thenReturn(USER_ID);
        when(rs.getString("claim_state")).thenReturn("ACTIVE");
        when(rs.getTimestamp("activated_at")).thenReturn(Timestamp.from(originalActivation));
        when(rs.getObject("subscription_id", UUID.class)).thenReturn(SUBSCRIPTION_ID);
        when(rs.getString("subscription_state")).thenReturn("ACTIVE");
        when(rs.getTimestamp("ends_at")).thenReturn(Timestamp.from(originalEnd));
        when(rs.getObject("grant_id", UUID.class)).thenReturn(GRANT_ID);
        when(rs.getLong("duration_seconds")).thenReturn(Duration.ofHours(24).toSeconds());
        when(rs.getString("grant_state")).thenReturn("ACTIVE");
        when(jdbc.query(anyString(), any(RowMapper.class), eq(claimId))).thenAnswer(invocation -> {
            RowMapper mapper = invocation.getArgument(1);
            return List.of(mapper.mapRow(rs, 0));
        });

        ActivatedSubscription result = activations.activateTrial(claimId, ACTIVATED_AT);

        assertEquals(originalActivation, result.activatedAt());
        assertEquals(originalEnd, result.endsAt());
        verify(jdbc, never()).update(anyString(), any(Object[].class));
    }

    @SuppressWarnings({"rawtypes", "unchecked"})
    private void stubGrant(
            String sourceType,
            String grantState,
            String subscriptionState,
            Instant subscriptionEnd,
            Instant grantStart,
            Instant commercialConfirmation) throws Exception {
        ResultSet rs = mock(ResultSet.class);
        when(rs.getObject("grant_id", UUID.class)).thenReturn(GRANT_ID);
        when(rs.getObject("subscription_id", UUID.class)).thenReturn(SUBSCRIPTION_ID);
        when(rs.getString("source_type")).thenReturn(sourceType);
        when(rs.getLong("duration_seconds")).thenReturn(Duration.ofDays(30).toSeconds());
        when(rs.getString("grant_state")).thenReturn(grantState);
        when(rs.getTimestamp("starts_at")).thenReturn(grantStart == null ? null : Timestamp.from(grantStart));
        when(rs.getTimestamp("ends_at")).thenReturn(grantStart == null ? null
                : Timestamp.from(grantStart.plus(Duration.ofDays(30))));
        when(rs.getTimestamp("confirmed_at")).thenReturn(
                commercialConfirmation == null ? null : Timestamp.from(commercialConfirmation));
        when(rs.getString("subscription_state")).thenReturn(subscriptionState);
        when(rs.getTimestamp("subscription_ends_at")).thenReturn(
                subscriptionEnd == null ? null : Timestamp.from(subscriptionEnd));
        when(jdbc.query(anyString(), any(RowMapper.class), eq(GRANT_ID))).thenAnswer(invocation -> {
            RowMapper mapper = invocation.getArgument(1);
            return List.of(mapper.mapRow(rs, 0));
        });
    }
}
