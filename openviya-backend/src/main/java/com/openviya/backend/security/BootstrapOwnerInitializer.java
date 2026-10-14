package com.openviya.backend.security;

import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import java.util.Locale;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;

@Component
@ConditionalOnProperty(name = "openviya.auth.bootstrap-owner.enabled", havingValue = "true")
public class BootstrapOwnerInitializer implements ApplicationRunner {

    private final StaffAuthRepository repository;
    private final PasswordEncoder passwordEncoder;
    private final AuditEventRepository auditEvents;
    private final String login;
    private final String password;

    public BootstrapOwnerInitializer(
            StaffAuthRepository repository,
            PasswordEncoder passwordEncoder,
            AuditEventRepository auditEvents,
            @Value("${openviya.auth.bootstrap-owner.login:}") String login,
            @Value("${openviya.auth.bootstrap-owner.password:}") String password) {
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
                || password.getBytes(java.nio.charset.StandardCharsets.UTF_8).length > 72) {
            throw new IllegalStateException(
                    "Bootstrap owner requires a login and a one-time password of 16 to 72 UTF-8 bytes");
        }
        if (repository.ownerExists()) {
            throw new IllegalStateException(
                    "An owner already exists; disable and remove bootstrap owner credentials before startup");
        }

        var staffId = repository.createBootstrapOwner(login.strip(), normalizedLogin, passwordEncoder.encode(password));
        auditEvents.append(
                new AuditActor(AuditActor.Kind.SYSTEM, null),
                "staff.owner.bootstrapped", "STAFF", staffId.toString(),
                "Initial owner created by one-time deployment bootstrap", null,
                java.util.Map.of("status", "PENDING", "role", "OWNER"), null);
    }
}
