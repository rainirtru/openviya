package com.openviya.backend.security;

import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotEmpty;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Pattern;
import jakarta.validation.constraints.Size;
import java.util.Set;
import java.util.UUID;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.PatchMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;
import com.openviya.backend.api.CorrelationIdFilter;
import jakarta.servlet.http.HttpServletRequest;

@RestController
@RequestMapping("/api/v1/admin/staff")
public class StaffAdminController {

    private final StaffAdministrationService service;

    public StaffAdminController(StaffAdministrationService service) {
        this.service = service;
    }

    @PostMapping
    public StaffAdministrationService.StaffRecord create(
            @Valid @RequestBody CreateStaffRequest request,
            @AuthenticationPrincipal StaffPrincipal actor,
            HttpServletRequest servletRequest) {
        try {
            return service.create(actor, request.login(), request.password(), request.roles(), request.reason(),
                    correlationId(servletRequest));
        } catch (DuplicateKeyException exception) {
            throw new StaffAdminException(HttpStatus.CONFLICT, "STAFF_LOGIN_ALREADY_EXISTS");
        }
    }

    @PutMapping("/{id}/roles")
    public StaffAdministrationService.StaffRecord replaceRoles(
            @PathVariable UUID id,
            @Valid @RequestBody RolesRequest request,
            @AuthenticationPrincipal StaffPrincipal actor,
            HttpServletRequest servletRequest) {
        return service.replaceRoles(actor, id, request.roles(), request.reason(), correlationId(servletRequest));
    }

    @PatchMapping("/{id}/status")
    public StaffAdministrationService.StaffRecord changeStatus(
            @PathVariable UUID id,
            @Valid @RequestBody StatusRequest request,
            @AuthenticationPrincipal StaffPrincipal actor,
            HttpServletRequest servletRequest) {
        return service.changeStatus(actor, id, request.status(), request.reason(), correlationId(servletRequest));
    }

    private static String correlationId(HttpServletRequest request) {
        Object value = request.getAttribute(CorrelationIdFilter.ATTRIBUTE_NAME);
        return value instanceof String string ? string : null;
    }

    public record CreateStaffRequest(
            @NotBlank @Size(max = 120) @Pattern(regexp = "[^\\r\\n]+") String login,
            @NotBlank @Size(min = 16, max = 256) String password,
            @NotEmpty Set<@NotBlank String> roles,
            @NotBlank @Size(max = 1000) String reason) {
    }

    public record RolesRequest(@NotEmpty Set<@NotBlank String> roles,
            @NotBlank @Size(max = 1000) String reason) {
    }

    public record StatusRequest(@NotNull String status,
            @NotBlank @Size(max = 1000) String reason) {
    }
}
