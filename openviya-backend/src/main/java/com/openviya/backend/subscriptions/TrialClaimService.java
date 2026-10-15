package com.openviya.backend.subscriptions;

import com.openviya.backend.operations.OperationJobRepository;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.util.List;
import java.util.UUID;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

@Service
public class TrialClaimService {

    private static final String JOB_KIND = "TRIAL_ACTIVATION";
    private final JdbcTemplate jdbc;
    private final OperationJobRepository jobs;

    public TrialClaimService(JdbcTemplate jdbc, OperationJobRepository jobs) {
        this.jdbc = jdbc;
        this.jobs = jobs;
    }

    @Transactional
    public TrialClaim claim(UUID userId, String sessionTelegramUserId) {
        requireActiveTelegramIdentity(userId, sessionTelegramUserId);

        TrialClaim existing = findClaim(sessionTelegramUserId);
        if (existing != null) {
            if (!existing.userId().equals(userId)) {
                throw new TrialClaimException(HttpStatus.CONFLICT, "TELEGRAM_IDENTITY_CONFLICT");
            }
            return existing;
        }

        UUID candidateSubscriptionId = UUID.randomUUID();
        jdbc.update("""
                INSERT INTO user_subscription (id, user_id)
                VALUES (?, ?)
                ON CONFLICT (user_id) DO NOTHING
                """, candidateSubscriptionId, userId);

        SubscriptionRow subscription = jdbc.queryForObject("""
                SELECT id, state
                FROM user_subscription
                WHERE user_id = ?
                FOR UPDATE
                """, (rs, rowNum) -> new SubscriptionRow(
                rs.getObject("id", UUID.class), rs.getString("state")), userId);
        if (!"INACTIVE".equals(subscription.state())) {
            existing = findClaim(sessionTelegramUserId);
            if (existing != null && existing.userId().equals(userId)) {
                return existing;
            }
            throw new TrialClaimException(HttpStatus.CONFLICT, "TRIAL_NOT_AVAILABLE");
        }

        UUID claimId = UUID.randomUUID();
        List<TrialClaim> inserted = jdbc.query("""
                INSERT INTO trial_claim (id, telegram_user_id, user_id, subscription_id)
                VALUES (?, ?, ?, ?)
                ON CONFLICT (telegram_user_id) DO NOTHING
                RETURNING id, user_id, subscription_id, state, activated_at, created_at
                """, TrialClaimService::mapClaim, claimId, sessionTelegramUserId, userId, subscription.id());
        if (inserted.isEmpty()) {
            existing = findClaim(sessionTelegramUserId);
            if (existing != null && existing.userId().equals(userId)) {
                return existing;
            }
            throw new TrialClaimException(HttpStatus.CONFLICT, "TRIAL_NOT_AVAILABLE");
        }

        TrialClaim claim = inserted.getFirst();
        jdbc.update("""
                UPDATE user_subscription
                SET state = 'PENDING', device_limit = ?, version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = ? AND state = 'INACTIVE'
                """, SubscriptionTime.TRIAL_DEVICE_LIMIT, subscription.id());
        jdbc.update("""
                INSERT INTO subscription_grant
                    (id, subscription_id, source_type, source_id, duration_seconds)
                VALUES (?, ?, 'TRIAL', ?, ?)
                """, UUID.randomUUID(), subscription.id(), claim.id(), SubscriptionTime.TRIAL_DURATION.toSeconds());
        jobs.enqueue(JOB_KIND, claim.id().toString(), 1,
                "{\"trialClaimId\":\"" + claim.id() + "\",\"userId\":\"" + userId + "\"}");
        return claim;
    }

    @Transactional(readOnly = true)
    public TrialClaim get(UUID userId, UUID claimId) {
        List<TrialClaim> claims = jdbc.query("""
                SELECT id, user_id, subscription_id, state, activated_at, created_at
                FROM trial_claim
                WHERE id = ? AND user_id = ?
                """, TrialClaimService::mapClaim, claimId, userId);
        if (claims.isEmpty()) {
            throw new TrialClaimException(HttpStatus.NOT_FOUND, "ACCESS_OPERATION_NOT_FOUND");
        }
        return claims.getFirst();
    }

    private void requireActiveTelegramIdentity(UUID userId, String sessionTelegramUserId) {
        List<String> ids = jdbc.query("""
                SELECT identity.provider_subject
                FROM app_user user_record
                JOIN external_identity identity ON identity.user_id = user_record.id
                WHERE user_record.id = ? AND user_record.status = 'ACTIVE'
                  AND identity.provider = 'TELEGRAM'
                FOR UPDATE OF user_record, identity
                """, (rs, rowNum) -> rs.getString(1), userId);
        if (ids.isEmpty()) {
            throw new TrialClaimException(HttpStatus.FORBIDDEN, "TELEGRAM_IDENTITY_REQUIRED");
        }
        if (!ids.getFirst().equals(sessionTelegramUserId)) {
            throw new TrialClaimException(HttpStatus.FORBIDDEN, "TELEGRAM_IDENTITY_MISMATCH");
        }
    }

    private TrialClaim findClaim(String telegramUserId) {
        List<TrialClaim> claims = jdbc.query("""
                SELECT id, user_id, subscription_id, state, activated_at, created_at
                FROM trial_claim
                WHERE telegram_user_id = ?
                """, TrialClaimService::mapClaim, telegramUserId);
        return claims.stream().findFirst().orElse(null);
    }

    private static TrialClaim mapClaim(ResultSet rs, int rowNum) throws SQLException {
        return new TrialClaim(
                rs.getObject("id", UUID.class),
                rs.getObject("user_id", UUID.class),
                rs.getObject("subscription_id", UUID.class),
                TrialClaimState.valueOf(rs.getString("state")),
                rs.getTimestamp("activated_at") == null ? null : rs.getTimestamp("activated_at").toInstant(),
                rs.getTimestamp("created_at").toInstant());
    }

    private record SubscriptionRow(UUID id, String state) {
    }
}
