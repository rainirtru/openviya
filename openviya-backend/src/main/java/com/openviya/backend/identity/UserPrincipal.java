package com.openviya.backend.identity;

import java.io.Serializable;
import java.util.UUID;
import org.springframework.security.core.AuthenticatedPrincipal;

public record UserPrincipal(UUID userId, String telegramUserId, String username, String displayName)
        implements Serializable, AuthenticatedPrincipal {
    @Override
    public String getName() {
        return userId.toString();
    }
}
