package com.openviya.backend.subscriptions;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

@Service
public class SubscriptionActivationService {

    private final JdbcTemplate jdbc;

    public SubscriptionActivationService(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    @Transactional
    public ActivatedSubscription activateTrial(UUID trialClaimId, Instant confirmedAt) {
        if (trialClaimId == null || confirmedAt == null) {
            throw new IllegalArgumentException("Trial claim and confirmation time are required");
        }

        List<TrialActivation> activations = jdbc.query("""
                SELECT claim.user_id, claim.state AS claim_state,
                       claim.activated_at, subscription.id AS subscription_id,
                       subscription.state AS subscription_state, subscription.ends_at,
                       grant_record.id AS grant_id, grant_record.duration_seconds,
                       grant_record.state AS grant_state
                FROM trial_claim claim
                JOIN user_subscription subscription ON subscription.id = claim.subscription_id
                JOIN subscription_grant grant_record
                  ON grant_record.source_type = 'TRIAL' AND grant_record.source_id = claim.id
                WHERE claim.id = ?
                FOR UPDATE OF claim, subscription, grant_record
                """, SubscriptionActivationService::mapActivation, trialClaimId);
        if (activations.isEmpty()) {
            throw new SubscriptionActivationException("TRIAL_ACTIVATION_NOT_FOUND");
        }

        TrialActivation activation = activations.getFirst();
        if (activation.claimState() == TrialClaimState.ACTIVE) {
            return new ActivatedSubscription(
                    activation.subscriptionId(), activation.userId(), activation.activatedAt(), activation.endsAt());
        }
        if (activation.claimState() == TrialClaimState.SUPERSEDED
                || !"PENDING".equals(activation.subscriptionState())
                || !"PENDING".equals(activation.grantState())) {
            throw new SubscriptionActivationException("TRIAL_ACTIVATION_SUPERSEDED");
        }

        Instant end = SubscriptionTime.endAfter(
                activation.endsAt(), confirmedAt, Duration.ofSeconds(activation.durationSeconds()));
        int grantUpdated = jdbc.update("""
                UPDATE subscription_grant
                SET state = 'ACTIVE', starts_at = ?, ends_at = ?
                WHERE id = ? AND state = 'PENDING'
                """, confirmedAt, end, activation.grantId());
        int subscriptionUpdated = jdbc.update("""
                UPDATE user_subscription
                SET state = 'ACTIVE', ends_at = ?, device_limit = ?, version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state = 'PENDING'
                """, end, SubscriptionTime.TRIAL_DEVICE_LIMIT, activation.subscriptionId());
        int claimUpdated = jdbc.update("""
                UPDATE trial_claim
                SET state = 'ACTIVE', activated_at = ?, last_error_code = NULL,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state IN ('REQUESTED', 'ISSUING', 'FAILED')
                """, confirmedAt, trialClaimId);
        if (grantUpdated != 1 || subscriptionUpdated != 1 || claimUpdated != 1) {
            throw new SubscriptionActivationException("TRIAL_ACTIVATION_CONFLICT");
        }
        return new ActivatedSubscription(
                activation.subscriptionId(), activation.userId(), confirmedAt, end);
    }

    private static TrialActivation mapActivation(ResultSet rs, int rowNum) throws SQLException {
        return new TrialActivation(
                rs.getObject("user_id", UUID.class),
                TrialClaimState.valueOf(rs.getString("claim_state")),
                instantOrNull(rs, "activated_at"),
                rs.getObject("subscription_id", UUID.class),
                rs.getString("subscription_state"),
                instantOrNull(rs, "ends_at"),
                rs.getObject("grant_id", UUID.class),
                rs.getLong("duration_seconds"),
                rs.getString("grant_state"));
    }

    private static Instant instantOrNull(ResultSet rs, String column) throws SQLException {
        return rs.getTimestamp(column) == null ? null : rs.getTimestamp(column).toInstant();
    }

    private record TrialActivation(
            UUID userId,
            TrialClaimState claimState,
            Instant activatedAt,
            UUID subscriptionId,
            String subscriptionState,
            Instant endsAt,
            UUID grantId,
            long durationSeconds,
            String grantState) {
    }
}
