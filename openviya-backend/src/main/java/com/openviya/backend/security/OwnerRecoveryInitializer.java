package com.openviya.backend.security;

import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.Map;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;

@Component
@ConditionalOnProperty(name = "openviya.auth.owner-recovery.enabled", havingValue = "true")
public class OwnerRecoveryInitializer implements ApplicationRunner {

    private static final Logger logger = LoggerFactory.getLogger(OwnerRecoveryInitializer.class);

    private final StaffAuthRepository repository;
    private final PasswordEncoder passwordEncoder;
    private final AuditEventRepository auditEvents;
    private final String login;
    private final String password;

    public OwnerRecoveryInitializer(
            StaffAuthRepository repository,
            PasswordEncoder passwordEncoder,
            AuditEventRepository auditEvents,
            @Value("${openviya.auth.bootstrap-owner.enabled:false}") boolean bootstrapEnabled,
            @Value("${openviya.auth.owner-recovery.login:}") String login,
            @Value("${openviya.auth.owner-recovery.password:}") String password) {
        if (bootstrapEnabled) {
            throw new IllegalStateException("Owner bootstrap and owner recovery cannot be enabled together");
        }
        this.repository = repository;
        this.passwordEncoder = passwordEncoder;
        this.auditEvents = auditEvents;
        this.login = login;
        this.password = password;
    }

    @Override
    @Transactional
    public void run(ApplicationArguments args) {
        repository.lockOwnerBootstrap();
        String normalizedLogin = login == null ? "" : login.strip().toLowerCase(Locale.ROOT);
        if (normalizedLogin.isBlank() || normalizedLogin.length() > 120
                || password == null || password.length() < 16
                || password.getBytes(StandardCharsets.UTF_8).length > 72) {
            throw new IllegalStateException(
                    "Owner recovery requires an existing owner login and a one-time password of 16 to 72 UTF-8 bytes");
        }
        var owner = repository.findOwnerByLogin(normalizedLogin);
        if (owner != null && repository.ownerRecoveryAlreadyUsed(owner.id())) {
            logger.warn("Owner recovery was already applied; skipping this one-time action and disabling recovery is required");
            return;
        }
        if (owner == null || !"ACTIVE".equals(owner.status())) {
            throw new IllegalStateException("Owner recovery requires a matching active owner account");
        }

        repository.recoverOwner(owner.id(), passwordEncoder.encode(password));
        auditEvents.append(new AuditActor(AuditActor.Kind.SYSTEM, null),
                "staff.owner.recovered", "STAFF", owner.id().toString(),
                "One-time owner recovery; two-factor enrollment is required at next login",
                Map.of("status", owner.status()), Map.of("status", "PENDING", "twoFactor", "RESET"), null);
    }
}
