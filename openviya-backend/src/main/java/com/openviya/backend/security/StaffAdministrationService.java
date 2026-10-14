package com.openviya.backend.security;

import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;
import org.springframework.http.HttpStatus;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

@Service
public class StaffAdministrationService {

    private static final Set<String> ALLOWED_ROLES = Set.of("OWNER", "ADMIN", "SUPPORT");
    private static final Set<String> ALLOWED_STATUSES = Set.of("ACTIVE", "LOCKED", "DISABLED");

    private final StaffAuthRepository repository;
    private final PasswordEncoder passwordEncoder;
    private final AuditEventRepository auditEvents;

    public StaffAdministrationService(
            StaffAuthRepository repository, PasswordEncoder passwordEncoder, AuditEventRepository auditEvents) {
        this.repository = repository;
        this.passwordEncoder = passwordEncoder;
        this.auditEvents = auditEvents;
    }

    @Transactional
    public StaffRecord create(
            StaffPrincipal actor, String login, String password, Set<String> roles, String reason, String correlationId) {
        requireOwner(actor);
        var normalizedLogin = normalizeLogin(login);
        validatePassword(password);
        var normalizedRoles = normalizeRoles(roles);
        validateReason(reason);
        repository.lockRoleAdministration();
        UUID id = repository.createStaff(login.strip(), normalizedLogin, passwordEncoder.encode(password),
                actor.staffId(), normalizedRoles);
        var state = staffState(login.strip(), "PENDING", normalizedRoles);
        auditEvents.append(new AuditActor(AuditActor.Kind.STAFF, actor.staffId()),
                "staff.account.created", "STAFF", id.toString(), reason, null, state, correlationId);
        return new StaffRecord(id, login.strip(), "PENDING", normalizedRoles);
    }

    @Transactional
    public StaffRecord replaceRoles(
            StaffPrincipal actor, UUID id, Set<String> roles, String reason, String correlationId) {
        requireOwner(actor);
        var normalizedRoles = normalizeRoles(roles);
        validateReason(reason);
        repository.lockRoleAdministration();
        var target = repository.managedStaff(id);
        if (target == null) {
            throw new StaffAdminException(HttpStatus.NOT_FOUND, "STAFF_NOT_FOUND");
        }
        var oldRoles = new TreeSet<>(repository.rolesForManagement(id));
        if ("ACTIVE".equals(target.status()) && oldRoles.contains("OWNER")
                && !normalizedRoles.contains("OWNER") && repository.countOtherActiveOwners(id) == 0) {
            throw new StaffAdminException(HttpStatus.CONFLICT, "LAST_ACTIVE_OWNER_REQUIRED");
        }
        repository.replaceRoles(id, actor.staffId(), normalizedRoles);
        auditEvents.append(new AuditActor(AuditActor.Kind.STAFF, actor.staffId()),
                "staff.roles.replaced", "STAFF", id.toString(), reason,
                Map.of("roles", oldRoles), Map.of("roles", normalizedRoles), correlationId);
        return new StaffRecord(id, target.login(), target.status(), normalizedRoles);
    }

    @Transactional
    public StaffRecord changeStatus(
            StaffPrincipal actor, UUID id, String requestedStatus, String reason, String correlationId) {
        requireOwner(actor);
        var status = requestedStatus == null ? "" : requestedStatus.strip().toUpperCase(Locale.ROOT);
        if (!ALLOWED_STATUSES.contains(status)) {
            throw new StaffAdminException(HttpStatus.BAD_REQUEST, "INVALID_STAFF_STATUS");
        }
        validateReason(reason);
        repository.lockRoleAdministration();
        var target = repository.managedStaff(id);
        if (target == null) {
            throw new StaffAdminException(HttpStatus.NOT_FOUND, "STAFF_NOT_FOUND");
        }
        if (target.status().equals(status)) {
            return new StaffRecord(id, target.login(), target.status(),
                    new TreeSet<>(repository.rolesForManagement(id)));
        }
        var roles = new TreeSet<>(repository.rolesForManagement(id));
        if ("ACTIVE".equals(target.status()) && roles.contains("OWNER")
                && !"ACTIVE".equals(status) && repository.countOtherActiveOwners(id) == 0) {
            throw new StaffAdminException(HttpStatus.CONFLICT, "LAST_ACTIVE_OWNER_REQUIRED");
        }
        repository.changeStaffStatus(id, status);
        auditEvents.append(new AuditActor(AuditActor.Kind.STAFF, actor.staffId()),
                "staff.status.changed", "STAFF", id.toString(), reason,
                Map.of("status", target.status()), Map.of("status", status), correlationId);
        return new StaffRecord(id, target.login(), status, roles);
    }

    private static Map<String, ?> staffState(String login, String status, Set<String> roles) {
        return Map.of("login", login, "status", status, "roles", roles);
    }

    private static void requireOwner(StaffPrincipal actor) {
        if (actor == null || !actor.roles().contains("OWNER")) {
            throw new StaffAdminException(HttpStatus.FORBIDDEN, "OWNER_REQUIRED");
        }
    }

    private static String normalizeLogin(String login) {
        if (login == null || login.isBlank() || login.strip().length() > 120
                || login.strip().contains("\n") || login.strip().contains("\r")) {
            throw new StaffAdminException(HttpStatus.BAD_REQUEST, "INVALID_STAFF_LOGIN");
        }
        return login.strip().toLowerCase(Locale.ROOT);
    }

    private static void validatePassword(String password) {
        if (password == null || password.length() < 16
                || password.getBytes(StandardCharsets.UTF_8).length > 72) {
            throw new StaffAdminException(HttpStatus.BAD_REQUEST, "INVALID_STAFF_PASSWORD");
        }
    }

    private static Set<String> normalizeRoles(Set<String> roles) {
        if (roles == null || roles.isEmpty()) {
            throw new StaffAdminException(HttpStatus.BAD_REQUEST, "STAFF_ROLE_REQUIRED");
        }
        var normalized = new TreeSet<String>();
        for (String role : roles) {
            var candidate = role == null ? "" : role.strip().toUpperCase(Locale.ROOT);
            if (!ALLOWED_ROLES.contains(candidate)) {
                throw new StaffAdminException(HttpStatus.BAD_REQUEST, "INVALID_STAFF_ROLE");
            }
            normalized.add(candidate);
        }
        return Set.copyOf(normalized);
    }

    private static void validateReason(String reason) {
        if (reason == null || reason.isBlank() || reason.length() > 1000) {
            throw new StaffAdminException(HttpStatus.BAD_REQUEST, "AUDIT_REASON_REQUIRED");
        }
    }

    public record StaffRecord(UUID id, String login, String status, Set<String> roles) {
    }
}
