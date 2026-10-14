package com.openviya.backend.identity;

import java.io.Serializable;
import java.util.UUID;

public record UserPrincipal(UUID userId, String telegramUserId, String username, String displayName)
        implements Serializable {
}
