package com.openviya.backend.identity;

import java.time.Instant;
import java.util.UUID;

public record ExternalIdentity(
        UUID userId,
        Provider provider,
        String providerSubject,
        String username,
        String displayName,
        Instant verifiedAt) {

    public enum Provider {
        TELEGRAM,
        EMAIL
    }
}
