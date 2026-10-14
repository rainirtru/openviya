package com.openviya.backend.security;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.util.List;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Repository;

@Repository
public class StaffAuthRepository {

    private final JdbcTemplate jdbcTemplate;

    public StaffAuthRepository(JdbcTemplate jdbcTemplate) {
        this.jdbcTemplate = jdbcTemplate;
    }

    public Optional<LoginAccount> findLoginAccount(String normalizedLogin) {
        return jdbcTemplate.query(
                "SELECT id, login, login_normalized, password_hash, status, auth_version "
                        + "FROM staff_account WHERE login_normalized = ?",
                (rs, rowNum) -> new LoginAccount(
                        rs.getObject("id", UUID.class), rs.getString("login"),
                        rs.getString("login_normalized"), rs.getString("password_hash"),
                        rs.getString("status"), rs.getInt("auth_version")),
                normalizedLogin).stream().findFirst();
    }

    public boolean createChallenge(UUID challengeId, UUID staffId, int authVersion, long lifetimeSeconds) {
        return jdbcTemplate.update(
                """
                INSERT INTO staff_auth_challenge (id, staff_id, auth_version, password_verified_at, expires_at)
                SELECT ?, id, auth_version, clock_timestamp(), clock_timestamp() + (? * interval '1 second') FROM staff_account
                WHERE id = ? AND auth_version = ? AND status IN ('PENDING', 'ACTIVE')
                """,
                challengeId, lifetimeSeconds, staffId, authVersion) == 1;
    }

    public Challenge lockChallenge(UUID challengeId) {
        return jdbcTemplate.query(
                """
                SELECT c.id, c.staff_id, c.auth_version, c.expires_at, c.consumed_at, c.failed_attempts,
                       s.login, s.status, clock_timestamp() AS server_now
                FROM staff_auth_challenge c
                JOIN staff_account s ON s.id = c.staff_id
                WHERE c.id = ? AND c.auth_version = s.auth_version
                FOR UPDATE OF c, s
                """,
                (rs, rowNum) -> new Challenge(
                        rs.getObject("id", UUID.class), rs.getObject("staff_id", UUID.class),
                        rs.getTimestamp("expires_at").toInstant(), timestamp(rs, "consumed_at"),
                        rs.getInt("failed_attempts"), rs.getString("login"), rs.getString("status"),
                        rs.getInt("auth_version"), rs.getTimestamp("server_now").toInstant()),
                challengeId).stream().findFirst().orElse(null);
    }

    public TotpCredential lockTotpCredential(UUID staffId) {
        return jdbcTemplate.query(
                "SELECT secret_ciphertext, encryption_key_version, confirmed_at, last_accepted_step "
                        + "FROM staff_totp_credential WHERE staff_id = ? FOR UPDATE",
                (rs, rowNum) -> new TotpCredential(
                        rs.getBytes("secret_ciphertext"), rs.getString("encryption_key_version"),
                        timestamp(rs, "confirmed_at"), (Long) rs.getObject("last_accepted_step")),
                staffId).stream().findFirst().orElse(null);
    }

    public boolean saveUnconfirmedTotp(UUID staffId, TotpSecretCipher.EncryptedSecret secret) {
        return jdbcTemplate.update(
                """
                INSERT INTO staff_totp_credential
                    (staff_id, secret_ciphertext, encryption_key_version, confirmed_at, last_accepted_step)
                VALUES (?, ?, ?, NULL, NULL)
                ON CONFLICT (staff_id) DO UPDATE SET
                    secret_ciphertext = EXCLUDED.secret_ciphertext,
                    encryption_key_version = EXCLUDED.encryption_key_version,
                    created_at = clock_timestamp(),
                    last_accepted_step = NULL
                WHERE staff_totp_credential.confirmed_at IS NULL
                """,
                staffId, secret.ciphertext(), secret.keyVersion()) == 1;
    }

    public boolean confirmTotp(UUID staffId, long step) {
        return jdbcTemplate.update(
                "UPDATE staff_totp_credential SET confirmed_at = clock_timestamp(), last_accepted_step = ? "
                        + "WHERE staff_id = ? AND confirmed_at IS NULL",
                step, staffId) == 1;
    }

    public boolean acceptTotpStep(UUID staffId, long step, Long oldStep) {
        return jdbcTemplate.update(
                "UPDATE staff_totp_credential SET last_accepted_step = ? "
                        + "WHERE staff_id = ? AND confirmed_at IS NOT NULL "
                        + "AND last_accepted_step IS NOT DISTINCT FROM ? "
                        + "AND (last_accepted_step IS NULL OR last_accepted_step < ?)",
                step, staffId, oldStep, step) == 1;
    }

    public boolean consumeChallenge(UUID challengeId) {
        return jdbcTemplate.update(
                "UPDATE staff_auth_challenge SET consumed_at = clock_timestamp() "
                        + "WHERE id = ? AND consumed_at IS NULL",
                challengeId) == 1;
    }

    public int recordChallengeFailure(UUID challengeId) {
        return jdbcTemplate.update(
                "UPDATE staff_auth_challenge SET failed_attempts = failed_attempts + 1, "
                        + "consumed_at = CASE WHEN failed_attempts >= 4 THEN clock_timestamp() ELSE consumed_at END "
                        + "WHERE id = ? AND consumed_at IS NULL",
                challengeId);
    }

    public boolean activateStaff(UUID staffId) {
        return jdbcTemplate.update(
                "UPDATE staff_account SET status = 'ACTIVE', last_login_at = clock_timestamp(), "
                        + "updated_at = clock_timestamp() WHERE id = ? AND status IN ('PENDING', 'ACTIVE')",
                staffId) == 1;
    }

    public List<String> roles(UUID staffId) {
        return jdbcTemplate.queryForList(
                "SELECT role FROM staff_role WHERE staff_id = ? ORDER BY role",
                String.class, staffId);
    }

    public CurrentStaff currentStaff(UUID staffId) {
        return jdbcTemplate.query(
                "SELECT id, login, status, auth_version FROM staff_account WHERE id = ?",
                (rs, rowNum) -> new CurrentStaff(
                        rs.getObject("id", UUID.class), rs.getString("login"),
                        rs.getString("status"), rs.getInt("auth_version")),
                staffId).stream().findFirst().orElse(null);
    }

    public void insertRecoveryCode(UUID id, UUID staffId, String hash) {
        jdbcTemplate.update(
                "INSERT INTO staff_recovery_code (id, staff_id, code_hash) VALUES (?, ?, ?)", id, staffId, hash);
    }

    public boolean ownerExists() {
        Boolean exists = jdbcTemplate.queryForObject(
                "SELECT EXISTS (SELECT 1 FROM staff_role WHERE role = 'OWNER')", Boolean.class);
        return Boolean.TRUE.equals(exists);
    }

    public void lockOwnerBootstrap() {
        jdbcTemplate.execute("SELECT pg_advisory_xact_lock(7436244821)");
    }

    public UUID createBootstrapOwner(String login, String normalizedLogin, String passwordHash) {
        UUID staffId = UUID.randomUUID();
        jdbcTemplate.update(
                "INSERT INTO staff_account (id, login, login_normalized, password_hash, status) "
                        + "VALUES (?, ?, ?, ?, 'PENDING')",
                staffId, login, normalizedLogin, passwordHash);
        jdbcTemplate.update(
                "INSERT INTO staff_role (staff_id, role) VALUES (?, 'OWNER')", staffId);
        return staffId;
    }

    public UUID createStaff(String login, String normalizedLogin, String passwordHash, UUID assignedBy, Set<String> roles) {
        UUID staffId = UUID.randomUUID();
        jdbcTemplate.update(
                "INSERT INTO staff_account (id, login, login_normalized, password_hash, status) "
                        + "VALUES (?, ?, ?, ?, 'PENDING')",
                staffId, login, normalizedLogin, passwordHash);
        for (String role : roles) {
            jdbcTemplate.update(
                    "INSERT INTO staff_role (staff_id, role, assigned_by) VALUES (?, ?, ?)",
                    staffId, role, assignedBy);
        }
        return staffId;
    }

    public void lockRoleAdministration() {
        jdbcTemplate.execute("SELECT pg_advisory_xact_lock(7436244822)");
    }

    public ManagedStaff managedStaff(UUID staffId) {
        return jdbcTemplate.query(
                "SELECT id, login, status FROM staff_account WHERE id = ? FOR UPDATE",
                (rs, rowNum) -> new ManagedStaff(
                        rs.getObject("id", UUID.class), rs.getString("login"), rs.getString("status")),
                staffId).stream().findFirst().orElse(null);
    }

    public int countOtherActiveOwners(UUID excludedStaffId) {
        return jdbcTemplate.queryForObject(
                "SELECT count(*) FROM staff_role r JOIN staff_account s ON s.id = r.staff_id "
                        + "WHERE r.role = 'OWNER' AND s.status = 'ACTIVE' AND s.id <> ?",
                Integer.class, excludedStaffId);
    }

    public void replaceRoles(UUID staffId, UUID assignedBy, Set<String> roles) {
        jdbcTemplate.update("DELETE FROM staff_role WHERE staff_id = ?", staffId);
        for (String role : roles) {
            jdbcTemplate.update(
                    "INSERT INTO staff_role (staff_id, role, assigned_by) VALUES (?, ?, ?)",
                    staffId, role, assignedBy);
        }
        jdbcTemplate.update(
                "UPDATE staff_account SET auth_version = auth_version + 1, updated_at = clock_timestamp() WHERE id = ?",
                staffId);
    }

    public boolean changeStaffStatus(UUID staffId, String status) {
        return jdbcTemplate.update(
                "UPDATE staff_account SET status = ?, auth_version = auth_version + 1, "
                        + "updated_at = clock_timestamp() WHERE id = ?",
                status, staffId) == 1;
    }

    public List<String> rolesForManagement(UUID staffId) {
        return jdbcTemplate.queryForList(
                "SELECT role FROM staff_role WHERE staff_id = ? ORDER BY role", String.class, staffId);
    }

    public ManagedStaff findOwnerByLogin(String normalizedLogin) {
        return jdbcTemplate.query(
                "SELECT s.id, s.login, s.status FROM staff_account s "
                        + "JOIN staff_role r ON r.staff_id = s.id AND r.role = 'OWNER' "
                        + "WHERE s.login_normalized = ? FOR UPDATE OF s",
                (rs, rowNum) -> new ManagedStaff(
                        rs.getObject("id", UUID.class), rs.getString("login"), rs.getString("status")),
                normalizedLogin).stream().findFirst().orElse(null);
    }

    public boolean ownerRecoveryAlreadyUsed(UUID ownerId) {
        return Boolean.TRUE.equals(jdbcTemplate.queryForObject(
                "SELECT EXISTS (SELECT 1 FROM audit_event WHERE action = 'staff.owner.recovered' "
                        + "AND subject_kind = 'STAFF' AND subject_id = ?)",
                Boolean.class, ownerId.toString()));
    }

    public void recoverOwner(UUID ownerId, String passwordHash) {
        jdbcTemplate.update(
                "UPDATE staff_account SET password_hash = ?, status = 'PENDING', "
                        + "auth_version = auth_version + 1, password_changed_at = clock_timestamp(), "
                        + "updated_at = clock_timestamp() WHERE id = ?",
                passwordHash, ownerId);
        jdbcTemplate.update("DELETE FROM staff_totp_credential WHERE staff_id = ?", ownerId);
        jdbcTemplate.update("DELETE FROM staff_recovery_code WHERE staff_id = ?", ownerId);
        jdbcTemplate.update("DELETE FROM staff_auth_challenge WHERE staff_id = ?", ownerId);
    }

    public record ManagedStaff(UUID id, String login, String status) {
    }

    public List<RecoveryCode> lockRecoveryCodes(UUID staffId) {
        return jdbcTemplate.query(
                "SELECT id, code_hash FROM staff_recovery_code "
                        + "WHERE staff_id = ? AND consumed_at IS NULL FOR UPDATE",
                (rs, rowNum) -> new RecoveryCode(rs.getObject("id", UUID.class), rs.getString("code_hash")),
                staffId);
    }

    public boolean consumeRecoveryCode(UUID id) {
        return jdbcTemplate.update(
                "UPDATE staff_recovery_code SET consumed_at = clock_timestamp() "
                        + "WHERE id = ? AND consumed_at IS NULL", id) == 1;
    }

    private static Instant timestamp(ResultSet rs, String column) throws SQLException {
        var value = rs.getTimestamp(column);
        return value == null ? null : value.toInstant();
    }

    public record LoginAccount(UUID id, String login, String normalizedLogin, String passwordHash,
            String status, int authVersion) {
    }

    public record Challenge(UUID id, UUID staffId, Instant expiresAt, Instant consumedAt,
            int failedAttempts, String login, String status, int authVersion, Instant serverNow) {
    }

    public record TotpCredential(byte[] ciphertext, String keyVersion, Instant confirmedAt, Long lastAcceptedStep) {
    }

    public record RecoveryCode(UUID id, String hash) {
    }

    public record CurrentStaff(UUID id, String login, String status, int authVersion) {
    }
}
