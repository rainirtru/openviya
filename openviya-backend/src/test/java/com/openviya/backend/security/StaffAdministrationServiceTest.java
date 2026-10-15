package com.openviya.backend.security;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;

import com.openviya.backend.audit.AuditEventRepository;
import java.util.Set;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.http.HttpStatus;
import org.springframework.security.crypto.password.PasswordEncoder;

class StaffAdministrationServiceTest {

    private StaffAuthRepository repository;
    private PasswordEncoder passwordEncoder;
    private AuditEventRepository auditEvents;
    private StaffAdministrationService service;

    @BeforeEach
    void setUp() {
        repository = mock(StaffAuthRepository.class);
        passwordEncoder = mock(PasswordEncoder.class);
        auditEvents = mock(AuditEventRepository.class);
        service = new StaffAdministrationService(repository, passwordEncoder, auditEvents);
    }

    @Test
    void supportCannotCreateStaffOrReachRepositoryMutations() {
        StaffPrincipal support = new StaffPrincipal(UUID.randomUUID(), "support", 1, Set.of("SUPPORT"));

        StaffAdminException error = assertThrows(StaffAdminException.class,
                () -> service.create(support, "new-staff", "a-strong-test-password", Set.of("ADMIN"),
                        "Reason for test", "correlation"));

        assertEquals(HttpStatus.FORBIDDEN, error.status());
        assertEquals("OWNER_REQUIRED", error.code());
        verify(repository, never()).createStaff(org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.anyString(), org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.anySet());
        verify(auditEvents, never()).append(org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.anyString(), org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.any(),
                org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.any());
    }

    @Test
    void administrativeMutationRequiresAnAuditReason() {
        StaffPrincipal owner = new StaffPrincipal(UUID.randomUUID(), "owner", 1, Set.of("OWNER"));

        StaffAdminException error = assertThrows(StaffAdminException.class,
                () -> service.create(owner, "new-staff", "a-strong-test-password", Set.of("ADMIN"),
                        "   ", "correlation"));

        assertEquals(HttpStatus.BAD_REQUEST, error.status());
        assertEquals("AUDIT_REASON_REQUIRED", error.code());
        verify(repository, never()).createStaff(org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.anyString(), org.mockito.ArgumentMatchers.anyString(),
                org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.anySet());
    }

    @Test
    void activeLastOwnerCannotRemoveOwnOwnerRole() {
        UUID ownerId = UUID.randomUUID();
        StaffPrincipal owner = new StaffPrincipal(ownerId, "owner", 1, Set.of("OWNER"));
        org.mockito.Mockito.when(repository.managedStaff(ownerId)).thenReturn(
                new StaffAuthRepository.ManagedStaff(ownerId, "owner", "ACTIVE"));
        org.mockito.Mockito.when(repository.rolesForManagement(ownerId)).thenReturn(java.util.List.of("OWNER"));
        org.mockito.Mockito.when(repository.countOtherActiveOwners(ownerId)).thenReturn(0);

        StaffAdminException error = assertThrows(StaffAdminException.class,
                () -> service.replaceRoles(owner, ownerId, Set.of("ADMIN"), "Role change", "correlation"));

        assertEquals(HttpStatus.CONFLICT, error.status());
        assertEquals("LAST_ACTIVE_OWNER_REQUIRED", error.code());
        verify(repository, never()).replaceRoles(org.mockito.ArgumentMatchers.any(),
                org.mockito.ArgumentMatchers.any(), org.mockito.ArgumentMatchers.anySet());
    }
}
