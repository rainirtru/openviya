package com.openviya.backend.security;

import java.io.Serializable;
import java.util.Set;
import java.util.UUID;
import org.springframework.security.core.AuthenticatedPrincipal;

public record StaffPrincipal(UUID staffId, String login, int authVersion, Set<String> roles)
        implements Serializable, AuthenticatedPrincipal {
    public StaffPrincipal {
        roles = Set.copyOf(roles);
    }

    @Override
    public String getName() {
        return staffId.toString();
    }
}
