package com.openviya.backend.security;

import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import java.nio.charset.StandardCharsets;
import java.sql.Timestamp;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.time.Instant;
import java.util.HexFormat;
import java.util.Map;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Service;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.transaction.annotation.Transactional;

@Service
public class AuthRateLimitService {

    private final JdbcTemplate jdbcTemplate;
    private final AuditEventRepository auditEvents;

    public AuthRateLimitService(JdbcTemplate jdbcTemplate, AuditEventRepository auditEvents) {
        this.jdbcTemplate = jdbcTemplate;
        this.auditEvents = auditEvents;
    }

    @Transactional
    public boolean allow(String scope, String accountKey, String sourceKey, int attempts, Duration window, Duration block) {
        boolean sourceAllowed = record(scope + ":source:" + sourceKey, attempts * 3, window, block);
        return sourceAllowed && record(scope + ":account:" + accountKey, attempts, window, block);
    }

    @Scheduled(fixedDelay = 3_600_000L)
    @Transactional
    public void deleteExpiredBuckets() {
        jdbcTemplate.update("DELETE FROM auth_rate_limit WHERE updated_at < clock_timestamp() - interval '30 days'");
        jdbcTemplate.update("DELETE FROM staff_auth_challenge "
                + "WHERE expires_at < clock_timestamp() - interval '7 days' "
                + "OR consumed_at < clock_timestamp() - interval '7 days'");
    }

    private boolean record(String rawBucketKey, int maxAttempts, Duration window, Duration block) {
        String bucketKey = digest(rawBucketKey);
        jdbcTemplate.update(
                "INSERT INTO auth_rate_limit (bucket_key, window_start, attempts) VALUES (?, clock_timestamp(), 0) "
                        + "ON CONFLICT (bucket_key) DO NOTHING",
                bucketKey);
        LimitState state = jdbcTemplate.queryForObject(
                "SELECT window_start, attempts, blocked_until, clock_timestamp() AS now "
                        + "FROM auth_rate_limit WHERE bucket_key = ? FOR UPDATE",
                (rs, rowNum) -> new LimitState(
                        rs.getTimestamp("window_start").toInstant(),
                        rs.getInt("attempts"),
                        rs.getTimestamp("blocked_until") == null ? null : rs.getTimestamp("blocked_until").toInstant(),
                        rs.getTimestamp("now").toInstant()),
                bucketKey);

        if (state.blockedUntil() != null && state.blockedUntil().isAfter(state.now())) {
            return false;
        }
        boolean resetWindow = !state.windowStart().plus(window).isAfter(state.now());
        int nextAttempts = resetWindow ? 1 : state.attempts() + 1;
        Instant windowStart = resetWindow ? state.now() : state.windowStart();
        Instant blockedUntil = nextAttempts > maxAttempts ? state.now().plus(block) : null;
        jdbcTemplate.update(
                "UPDATE auth_rate_limit SET window_start = ?, attempts = ?, blocked_until = ?, "
                        + "updated_at = clock_timestamp() WHERE bucket_key = ?",
                Timestamp.from(windowStart), nextAttempts,
                blockedUntil == null ? null : Timestamp.from(blockedUntil), bucketKey);
        if (nextAttempts > maxAttempts) {
            auditEvents.append(
                    new AuditActor(AuditActor.Kind.SYSTEM, null),
                    "auth.rate_limit.exceeded", "AUTH_RATE_LIMIT", bucketKey,
                    "Shared authentication rate limit exceeded", null,
                    Map.of("attempts", nextAttempts), null);
        }
        return nextAttempts <= maxAttempts;
    }

    private static String digest(String value) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256")
                    .digest(value.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 is unavailable", exception);
        }
    }

    private record LimitState(Instant windowStart, int attempts, Instant blockedUntil, Instant now) {
    }
}
