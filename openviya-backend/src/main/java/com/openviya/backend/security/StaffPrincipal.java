package com.openviya.backend.security;

import java.io.Serializable;
import java.util.Set;
import java.util.UUID;

public record StaffPrincipal(UUID staffId, String login, int authVersion, Set<String> roles) implements Serializable {
    public StaffPrincipal {
        roles = Set.copyOf(roles);
    }
}
