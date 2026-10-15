package com.openviya.backend.subscriptions;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
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
                SET state = 'ACTIVE', starts_at = ?, ends_at = ?, confirmed_at = ?
                WHERE id = ? AND state = 'PENDING'
                """, Timestamp.from(confirmedAt), Timestamp.from(end), Timestamp.from(confirmedAt),
                activation.grantId());
        int subscriptionUpdated = jdbc.update("""
                UPDATE user_subscription
                SET state = 'ACTIVE', ends_at = ?, device_limit = ?, version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state = 'PENDING'
                """, Timestamp.from(end), SubscriptionTime.TRIAL_DEVICE_LIMIT, activation.subscriptionId());
        int claimUpdated = jdbc.update("""
                UPDATE trial_claim
                SET state = 'ACTIVE', activated_at = ?, last_error_code = NULL,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state IN ('REQUESTED', 'ISSUING', 'FAILED')
                """, Timestamp.from(confirmedAt), trialClaimId);
        if (grantUpdated != 1 || subscriptionUpdated != 1 || claimUpdated != 1) {
            throw new SubscriptionActivationException("TRIAL_ACTIVATION_CONFLICT");
        }
        return new ActivatedSubscription(
                activation.subscriptionId(), activation.userId(), confirmedAt, end);
    }

    /** Records an immutable commercial confirmation separately from access activation. */
    @Transactional
    public CommercialGrantConfirmation confirmCommercialGrant(UUID grantId, Instant confirmedAt, boolean paid) {
        if (grantId == null || confirmedAt == null) {
            throw new IllegalArgumentException("Grant and confirmation time are required");
        }
        int updated = jdbc.update("""
                UPDATE subscription_grant
                SET confirmed_at = COALESCE(confirmed_at, ?),
                    paid_at = CASE WHEN ? THEN COALESCE(paid_at, ?) ELSE paid_at END
                WHERE id = ? AND source_type IN ('PURCHASE', 'PROMO')
                  AND state IN ('PENDING', 'ACTIVE')
                  AND (? = FALSE OR source_type = 'PURCHASE')
                """, Timestamp.from(confirmedAt), paid, Timestamp.from(confirmedAt), grantId, paid);
        if (updated != 1) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_CONFIRMATION_REJECTED");
        }
        return jdbc.queryForObject("""
                SELECT id, confirmed_at, paid_at
                FROM subscription_grant
                WHERE id = ?
                """, (rs, rowNum) -> new CommercialGrantConfirmation(
                rs.getObject("id", UUID.class),
                instantOrNull(rs, "confirmed_at"),
                instantOrNull(rs, "paid_at")), grantId);
    }

    /**
     * Activates an already-confirmed purchase or promotion grant. Confirmation of the commercial
     * event is deliberately separate from activation so a future order flow can persist payment
     * before access is applied. The caller must have created the pending grant in its own
     * transaction before invoking this method.
     */
    @Transactional
    public ActivatedGrant activateCommercialGrant(UUID grantId, Instant activatedAt) {
        if (grantId == null || activatedAt == null) {
            throw new IllegalArgumentException("Grant and activation time are required");
        }

        List<CommercialGrant> grants = jdbc.query("""
                SELECT grant_record.id AS grant_id, grant_record.subscription_id,
                       grant_record.source_type, grant_record.duration_seconds,
                       grant_record.state AS grant_state, grant_record.starts_at, grant_record.ends_at,
                       grant_record.confirmed_at,
                       subscription.state AS subscription_state, subscription.ends_at AS subscription_ends_at
                FROM subscription_grant grant_record
                JOIN user_subscription subscription ON subscription.id = grant_record.subscription_id
                WHERE grant_record.id = ?
                FOR UPDATE OF grant_record, subscription
                """, SubscriptionActivationService::mapCommercialGrant, grantId);
        if (grants.isEmpty()) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_NOT_FOUND");
        }

        CommercialGrant grant = grants.getFirst();
        if (!"PURCHASE".equals(grant.sourceType()) && !"PROMO".equals(grant.sourceType())) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_SOURCE_NOT_COMMERCIAL");
        }
        if ("ACTIVE".equals(grant.grantState())) {
            return new ActivatedGrant(grant.grantId(), grant.subscriptionId(), grant.startsAt(), grant.endsAt());
        }
        if (!"PENDING".equals(grant.grantState())) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_NOT_PENDING");
        }
        if (grant.confirmedAt() == null) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_NOT_CONFIRMED");
        }
        if (!List.of("INACTIVE", "ACTIVE", "EXPIRED").contains(grant.subscriptionState())) {
            // In particular, do not silently choose the unresolved trial/purchase race policy.
            throw new SubscriptionActivationException("SUBSCRIPTION_ACCESS_CHANGE_PENDING");
        }

        Instant start = grant.subscriptionEndsAt() == null || grant.subscriptionEndsAt().isBefore(activatedAt)
                ? activatedAt
                : grant.subscriptionEndsAt();
        Instant end = start.plusSeconds(grant.durationSeconds());
        int grantUpdated = jdbc.update("""
                UPDATE subscription_grant
                SET state = 'ACTIVE', starts_at = ?, ends_at = ?
                WHERE id = ? AND state = 'PENDING'
                """, Timestamp.from(start), Timestamp.from(end), grant.grantId());
        int subscriptionUpdated = jdbc.update("""
                UPDATE user_subscription
                SET state = 'ACTIVE', ends_at = ?, device_limit = ?, version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state IN ('INACTIVE', 'ACTIVE', 'EXPIRED')
                """, Timestamp.from(end), SubscriptionTime.STANDARD_DEVICE_LIMIT, grant.subscriptionId());
        if (grantUpdated != 1 || subscriptionUpdated != 1) {
            throw new SubscriptionActivationException("SUBSCRIPTION_GRANT_ACTIVATION_CONFLICT");
        }
        return new ActivatedGrant(grant.grantId(), grant.subscriptionId(), start, end);
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

    private static CommercialGrant mapCommercialGrant(ResultSet rs, int rowNum) throws SQLException {
        return new CommercialGrant(
                rs.getObject("grant_id", UUID.class),
                rs.getObject("subscription_id", UUID.class),
                rs.getString("source_type"),
                rs.getLong("duration_seconds"),
                rs.getString("grant_state"),
                instantOrNull(rs, "starts_at"),
                instantOrNull(rs, "ends_at"),
                instantOrNull(rs, "confirmed_at"),
                rs.getString("subscription_state"),
                instantOrNull(rs, "subscription_ends_at"));
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

    private record CommercialGrant(
            UUID grantId,
            UUID subscriptionId,
            String sourceType,
            long durationSeconds,
            String grantState,
            Instant startsAt,
            Instant endsAt,
            Instant confirmedAt,
            String subscriptionState,
            Instant subscriptionEndsAt) {
    }

    public record CommercialGrantConfirmation(UUID grantId, Instant confirmedAt, Instant paidAt) {
    }
}
