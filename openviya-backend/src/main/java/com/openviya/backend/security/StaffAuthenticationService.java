package com.openviya.backend.security;

import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.UUID;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.http.HttpStatus;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

@Service
public class StaffAuthenticationService {

    private static final Duration PASSWORD_WINDOW = Duration.ofMinutes(15);
    private static final Duration MFA_WINDOW = Duration.ofMinutes(5);
    private static final Duration RATE_LIMIT_BLOCK = Duration.ofMinutes(15);
    private static final int PASSWORD_ATTEMPTS = 10;
    private static final int MFA_ATTEMPTS = 5;
    private static final int MAX_CHALLENGE_FAILURES = 5;
    private static final String RECOVERY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private final StaffAuthRepository repository;
    private final AuthRateLimitService rateLimits;
    private final PasswordEncoder passwordEncoder;
    private final TotpSecretCipher secretCipher;
    private final TotpSupport totpSupport;
    private final AuditEventRepository auditEvents;
    private final Duration challengeLifetime;
    private final String dummyPasswordHash;
    private final SecureRandom secureRandom = new SecureRandom();

    public StaffAuthenticationService(
            StaffAuthRepository repository,
            AuthRateLimitService rateLimits,
            PasswordEncoder passwordEncoder,
            TotpSecretCipher secretCipher,
            TotpSupport totpSupport,
            AuditEventRepository auditEvents,
            @Value("${openviya.auth.challenge-lifetime:5m}") Duration challengeLifetime) {
        this.repository = repository;
        this.rateLimits = rateLimits;
        this.passwordEncoder = passwordEncoder;
        this.secretCipher = secretCipher;
        this.totpSupport = totpSupport;
        this.auditEvents = auditEvents;
        if (challengeLifetime.isNegative() || challengeLifetime.isZero()
                || challengeLifetime.compareTo(Duration.ofMinutes(10)) > 0) {
            throw new IllegalArgumentException("Staff authentication challenge lifetime must be between 1 second and 10 minutes");
        }
        this.challengeLifetime = challengeLifetime;
        this.dummyPasswordHash = passwordEncoder.encode(UUID.randomUUID().toString());
    }

    public PasswordChallenge beginPasswordLogin(String login, String password, String source) {
        String normalizedLogin = normalizeLogin(login);
        if (!rateLimits.allow("staff-password", normalizedLogin, normalizeSource(source),
                PASSWORD_ATTEMPTS, PASSWORD_WINDOW, RATE_LIMIT_BLOCK)) {
            throw new StaffAuthException(HttpStatus.TOO_MANY_REQUESTS, "AUTH_RATE_LIMITED");
        }

        StaffAuthRepository.LoginAccount account = repository.findLoginAccount(normalizedLogin).orElse(null);
        String candidatePassword = password == null ? "" : password;
        boolean passwordMatches = passwordEncoder.matches(candidatePassword,
                account == null ? dummyPasswordHash : account.passwordHash())
                && candidatePassword.getBytes(StandardCharsets.UTF_8).length <= 72;
        if (!passwordMatches || account == null
                || !(account.status().equals("PENDING") || account.status().equals("ACTIVE"))) {
            throw invalidCredentials();
        }

        StaffAuthRepository.TotpCredential credential = repository.lockTotpCredential(account.id());
        boolean needsEnrollment = credential == null || credential.confirmedAt() == null;
        if (account.status().equals("ACTIVE") && needsEnrollment) {
            throw invalidCredentials();
        }
        UUID challengeId = UUID.randomUUID();
        if (!repository.createChallenge(
                challengeId, account.id(), account.authVersion(), challengeLifetime.toSeconds())) {
            throw invalidCredentials();
        }
        return new PasswordChallenge(challengeId, needsEnrollment, challengeLifetime.toSeconds());
    }

    @Transactional
    public TotpEnrollment beginTotpEnrollment(UUID challengeId, String source) {
        StaffAuthRepository.Challenge challenge = requireLiveChallenge(challengeId);
        if (!challenge.status().equals("PENDING")) {
            throw invalidChallenge();
        }
        if (!rateLimits.allow("staff-enrollment", challenge.staffId().toString(), normalizeSource(source),
                MFA_ATTEMPTS, MFA_WINDOW, RATE_LIMIT_BLOCK)) {
            throw new StaffAuthException(HttpStatus.TOO_MANY_REQUESTS, "AUTH_RATE_LIMITED");
        }
        StaffAuthRepository.TotpCredential current = repository.lockTotpCredential(challenge.staffId());
        if (current != null && current.confirmedAt() != null) {
            throw invalidChallenge();
        }

        byte[] secret;
        if (current == null) {
            secret = totpSupport.newSecret();
            TotpSecretCipher.EncryptedSecret encrypted = secretCipher.encrypt(challenge.staffId(), secret);
            if (!repository.saveUnconfirmedTotp(challenge.staffId(), encrypted)) {
                throw invalidChallenge();
            }
            auditEvents.append(
                    new AuditActor(AuditActor.Kind.STAFF, challenge.staffId()),
                    "staff.totp.enrollment_started", "STAFF", challenge.staffId().toString(),
                    "Staff two-factor enrollment started", null, null, null);
        } else {
            secret = secretCipher.decrypt(challenge.staffId(), current.ciphertext(), current.keyVersion());
        }
        String manualEntryKey = totpSupport.toBase32(secret);
        String provisioningUri = totpSupport.provisioningUri(challenge.login(), secret);
        java.util.Arrays.fill(secret, (byte) 0);
        return new TotpEnrollment(challengeId, manualEntryKey, provisioningUri);
    }

    @Transactional
    public Completion completeTwoFactor(UUID challengeId, String code, String source, String correlationId) {
        StaffAuthRepository.Challenge challenge = requireLiveChallenge(challengeId);
        Set<String> roles = Set.copyOf(repository.roles(challenge.staffId()));
        if (roles.isEmpty()) {
            throw invalidChallenge();
        }
        if (!rateLimits.allow("staff-mfa", challenge.staffId().toString(), normalizeSource(source),
                MFA_ATTEMPTS, MFA_WINDOW, RATE_LIMIT_BLOCK)) {
            return Completion.rateLimited();
        }
        StaffAuthRepository.TotpCredential credential = repository.lockTotpCredential(challenge.staffId());
        if (credential == null) {
            return Completion.enrollmentRequired();
        }

        Long acceptedStep = null;
        boolean isEnrollment = credential.confirmedAt() == null;
        if (isEnrollment) {
            byte[] secret = secretCipher.decrypt(challenge.staffId(), credential.ciphertext(), credential.keyVersion());
            acceptedStep = totpSupport.matchingStep(secret, code, Instant.now(), credential.lastAcceptedStep());
            java.util.Arrays.fill(secret, (byte) 0);
        } else {
            byte[] secret = secretCipher.decrypt(challenge.staffId(), credential.ciphertext(), credential.keyVersion());
            acceptedStep = totpSupport.matchingStep(secret, code, Instant.now(), credential.lastAcceptedStep());
            java.util.Arrays.fill(secret, (byte) 0);
            if (acceptedStep == null) {
                String normalizedCode = normalizeRecoveryCode(code);
                if (normalizedCode != null) {
                    for (StaffAuthRepository.RecoveryCode recovery : repository.lockRecoveryCodes(challenge.staffId())) {
                        if (MessageDigest.isEqual(
                                recovery.hash().getBytes(StandardCharsets.US_ASCII),
                                recoveryHash(challenge.staffId(), normalizedCode).getBytes(StandardCharsets.US_ASCII))) {
                            if (repository.consumeRecoveryCode(recovery.id())) {
                                acceptedStep = -1L;
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (acceptedStep == null) {
            repository.recordChallengeFailure(challengeId);
            return Completion.invalidCode();
        }
        if (isEnrollment) {
            if (!repository.confirmTotp(challenge.staffId(), acceptedStep)) {
                return Completion.invalidChallenge();
            }
        } else if (acceptedStep >= 0
                && !repository.acceptTotpStep(challenge.staffId(), acceptedStep, credential.lastAcceptedStep())) {
            repository.recordChallengeFailure(challengeId);
            return Completion.invalidCode();
        }
        if (!repository.consumeChallenge(challengeId)) {
            return Completion.invalidChallenge();
        }
        if (!repository.activateStaff(challenge.staffId())) {
            return Completion.invalidChallenge();
        }

        boolean recoveryCodeUsed = acceptedStep < 0;
        List<String> recoveryCodes = isEnrollment ? createRecoveryCodes(challenge.staffId()) : List.of();
        auditEvents.append(
                new AuditActor(AuditActor.Kind.STAFF, challenge.staffId()),
                isEnrollment ? "staff.totp.enrollment_completed"
                        : recoveryCodeUsed ? "staff.login.recovery_code_used" : "staff.login.completed",
                "STAFF", challenge.staffId().toString(),
                isEnrollment ? "Staff two-factor enrollment confirmed" : "Staff login completed",
                null, null, correlationId);
        StaffPrincipal principal = new StaffPrincipal(
                challenge.staffId(), challenge.login(), challenge.authVersion(), roles);
        return Completion.success(principal, recoveryCodes);
    }

    private List<String> createRecoveryCodes(UUID staffId) {
        List<String> codes = new ArrayList<>();
        for (int i = 0; i < 8; i++) {
            StringBuilder raw = new StringBuilder(20);
            for (int n = 0; n < 20; n++) {
                raw.append(RECOVERY_ALPHABET.charAt(secureRandom.nextInt(RECOVERY_ALPHABET.length())));
            }
            String code = raw.substring(0, 5) + "-" + raw.substring(5, 10) + "-"
                    + raw.substring(10, 15) + "-" + raw.substring(15, 20);
            codes.add(code);
            repository.insertRecoveryCode(UUID.randomUUID(), staffId, recoveryHash(staffId, code.replace("-", "")));
        }
        return List.copyOf(codes);
    }

    private StaffAuthRepository.Challenge requireLiveChallenge(UUID challengeId) {
        if (challengeId == null) {
            throw invalidChallenge();
        }
        StaffAuthRepository.Challenge challenge = repository.lockChallenge(challengeId);
        if (challenge == null || challenge.consumedAt() != null
                || !challenge.expiresAt().isAfter(challenge.serverNow())
                || challenge.failedAttempts() >= MAX_CHALLENGE_FAILURES
                || !(challenge.status().equals("PENDING") || challenge.status().equals("ACTIVE"))) {
            throw invalidChallenge();
        }
        return challenge;
    }

    private static String normalizeLogin(String login) {
        if (login == null || login.isBlank() || login.length() > 120) {
            throw invalidCredentials();
        }
        return login.strip().toLowerCase(Locale.ROOT);
    }

    private static String normalizeSource(String source) {
        return source == null || source.isBlank() ? "unknown" : source.substring(0, Math.min(100, source.length()));
    }

    private static String normalizeRecoveryCode(String code) {
        if (code == null) {
            return null;
        }
        String normalized = code.replace("-", "").replace(" ", "").toUpperCase(Locale.ROOT);
        return normalized.matches("[A-HJ-NP-Z2-9]{20}") ? normalized : null;
    }

    private static String recoveryHash(UUID staffId, String code) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256")
                    .digest((staffId + ":" + code).getBytes(StandardCharsets.US_ASCII)));
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 is unavailable", exception);
        }
    }

    private static StaffAuthException invalidCredentials() {
        return new StaffAuthException(HttpStatus.UNAUTHORIZED, "INVALID_CREDENTIALS");
    }

    private static StaffAuthException invalidChallenge() {
        return new StaffAuthException(HttpStatus.UNAUTHORIZED, "INVALID_CHALLENGE");
    }

    public record PasswordChallenge(UUID challengeId, boolean totpEnrollmentRequired, long expiresInSeconds) {
    }

    public record TotpEnrollment(UUID challengeId, String manualEntryKey, String provisioningUri) {
    }

    public record Completion(String status, StaffPrincipal principal, List<String> recoveryCodes) {
        static Completion success(StaffPrincipal principal, List<String> recoveryCodes) {
            return new Completion("SUCCESS", principal, recoveryCodes);
        }

        static Completion invalidCode() {
            return new Completion("INVALID_CODE", null, List.of());
        }

        static Completion invalidChallenge() {
            return new Completion("INVALID_CHALLENGE", null, List.of());
        }

        static Completion enrollmentRequired() {
            return new Completion("ENROLLMENT_REQUIRED", null, List.of());
        }

        static Completion rateLimited() {
            return new Completion("RATE_LIMITED", null, List.of());
        }
    }
}
