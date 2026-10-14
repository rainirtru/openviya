package com.openviya.backend.identity;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.util.List;
import java.util.Objects;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Repository;
import org.springframework.transaction.annotation.Transactional;

@Repository
public class UserIdentityRepository {

    private final JdbcTemplate jdbcTemplate;

    public UserIdentityRepository(JdbcTemplate jdbcTemplate) {
        this.jdbcTemplate = jdbcTemplate;
    }

    /**
     * Resolves a Telegram profile to one internal user. Callers must pass a profile only after
     * verifying Telegram's signature/token and the login flow's state and nonce.
     */
    @Transactional
    public ExternalIdentity resolveTelegramIdentity(TelegramProfile profile, Instant verifiedAt) {
        Objects.requireNonNull(profile, "A verified Telegram profile is required");
        Objects.requireNonNull(verifiedAt, "A verification timestamp is required");
        UUID candidateUserId = UUID.randomUUID();
        jdbcTemplate.update("INSERT INTO app_user (id) VALUES (?)", candidateUserId);

        List<UUID> insertedIdentity = jdbcTemplate.query(
                """
                INSERT INTO external_identity
                    (id, user_id, provider, provider_subject, username, display_name, verified_at)
                VALUES (?, ?, 'TELEGRAM', ?, ?, ?, ?)
                ON CONFLICT (provider, provider_subject) DO NOTHING
                RETURNING user_id
                """,
                (rs, rowNum) -> rs.getObject("user_id", UUID.class),
                UUID.randomUUID(), candidateUserId, profile.telegramUserId(), profile.username(),
                profile.displayName(), verifiedAt);

        UUID resolvedUserId;
        if (insertedIdentity.isEmpty()) {
            jdbcTemplate.update("DELETE FROM app_user WHERE id = ?", candidateUserId);
            resolvedUserId = jdbcTemplate.queryForObject(
                    "SELECT user_id FROM external_identity WHERE provider = 'TELEGRAM' AND provider_subject = ?",
                    UUID.class,
                    profile.telegramUserId());
            if (resolvedUserId == null) {
                throw new IllegalStateException("Conflicting Telegram identity disappeared during resolution");
            }
            jdbcTemplate.update(
                    """
                    UPDATE external_identity
                    SET username = ?, display_name = ?, verified_at = ?, last_seen_at = clock_timestamp()
                    WHERE provider = 'TELEGRAM' AND provider_subject = ?
                    """,
                    profile.username(), profile.displayName(), verifiedAt, profile.telegramUserId());
        } else {
            resolvedUserId = insertedIdentity.getFirst();
        }

        if (!isActive(resolvedUserId)) {
            throw new UserIdentityException("USER_ACCOUNT_UNAVAILABLE");
        }

        return jdbcTemplate.queryForObject(
                """
                SELECT user_id, provider, provider_subject, username, display_name, verified_at
                FROM external_identity
                WHERE user_id = ? AND provider = 'TELEGRAM' AND provider_subject = ?
                """,
                UserIdentityRepository::mapIdentity,
                resolvedUserId,
                profile.telegramUserId());
    }

    public boolean isActive(UUID userId) {
        return Boolean.TRUE.equals(jdbcTemplate.query(
                "SELECT status = 'ACTIVE' FROM app_user WHERE id = ?",
                rs -> rs.next() && rs.getBoolean(1), userId));
    }

    private static ExternalIdentity mapIdentity(ResultSet rs, int rowNum) throws SQLException {
        return new ExternalIdentity(
                rs.getObject("user_id", UUID.class),
                ExternalIdentity.Provider.valueOf(rs.getString("provider")),
                rs.getString("provider_subject"),
                rs.getString("username"),
                rs.getString("display_name"),
                rs.getTimestamp("verified_at") == null ? null : rs.getTimestamp("verified_at").toInstant());
    }
}
