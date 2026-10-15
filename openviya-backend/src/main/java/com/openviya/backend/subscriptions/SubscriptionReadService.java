package com.openviya.backend.subscriptions;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

@Service
public class SubscriptionReadService {

    private final JdbcTemplate jdbc;
    private final Clock clock;

    public SubscriptionReadService(JdbcTemplate jdbc, Clock clock) {
        this.jdbc = jdbc;
        this.clock = clock;
    }

    @Transactional(readOnly = true)
    public SubscriptionSnapshot getForUser(UUID userId) {
        List<SubscriptionRow> subscriptions = jdbc.query("""
                SELECT id, state, ends_at, device_limit, version
                FROM user_subscription
                WHERE user_id = ?
                """, (rs, rowNum) -> new SubscriptionRow(
                rs.getObject("id", UUID.class),
                rs.getString("state"),
                instantOrNull(rs, "ends_at"),
                rs.getInt("device_limit"),
                rs.getLong("version")), userId);
        if (subscriptions.isEmpty()) {
            return new SubscriptionSnapshot(null, "INACTIVE", false, null, 0, 0,
                    hasTrialClaim(userId), false, false, List.of());
        }

        SubscriptionRow subscription = subscriptions.getFirst();
        List<SubscriptionSnapshot.GrantSnapshot> grants = jdbc.query("""
                SELECT source_type, state, duration_seconds, starts_at, ends_at,
                       confirmed_at, paid_at, created_at
                FROM subscription_grant
                WHERE subscription_id = ?
                ORDER BY created_at, id
                """, SubscriptionReadService::mapGrant, subscription.id());
        Instant now = clock.instant();
        boolean active = "ACTIVE".equals(subscription.state())
                && subscription.endsAt() != null
                && subscription.endsAt().isAfter(now);
        String effectiveState = "ACTIVE".equals(subscription.state()) && !active
                ? "EXPIRED"
                : subscription.state();
        return new SubscriptionSnapshot(
                subscription.id(), effectiveState, active, subscription.endsAt(),
                subscription.deviceLimit(), subscription.version(), hasTrialClaim(userId),
                grants.stream().anyMatch(grant -> !"TRIAL".equals(grant.sourceType()) && grant.startsAt() != null),
                grants.stream().anyMatch(grant -> grant.paidAt() != null), grants);
    }

    private boolean hasTrialClaim(UUID userId) {
        return Boolean.TRUE.equals(jdbc.queryForObject(
                "SELECT EXISTS (SELECT 1 FROM trial_claim WHERE user_id = ? AND state = 'ACTIVE')",
                Boolean.class,
                userId));
    }

    private static SubscriptionSnapshot.GrantSnapshot mapGrant(ResultSet rs, int rowNum) throws SQLException {
        return new SubscriptionSnapshot.GrantSnapshot(
                rs.getString("source_type"),
                rs.getString("state"),
                rs.getLong("duration_seconds"),
                instantOrNull(rs, "starts_at"),
                instantOrNull(rs, "ends_at"),
                instantOrNull(rs, "confirmed_at"),
                instantOrNull(rs, "paid_at"),
                rs.getTimestamp("created_at").toInstant());
    }

    private static Instant instantOrNull(ResultSet rs, String column) throws SQLException {
        return rs.getTimestamp(column) == null ? null : rs.getTimestamp(column).toInstant();
    }

    private record SubscriptionRow(UUID id, String state, Instant endsAt, int deviceLimit, long version) {
    }
}
