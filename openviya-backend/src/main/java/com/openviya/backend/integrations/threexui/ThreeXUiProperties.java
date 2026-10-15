package com.openviya.backend.integrations.threexui;

import java.net.URI;
import java.time.Duration;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

@Validated
@ConfigurationProperties("openviya.integrations.three-x-ui")
public record ThreeXUiProperties(
        boolean enabled,
        URI adminBaseUri,
        String bearerToken,
        Duration connectTimeout,
        Duration readTimeout) {

    public ThreeXUiProperties {
        if (enabled) {
            if (adminBaseUri == null || bearerToken == null || bearerToken.isBlank()) {
                throw new IllegalArgumentException("3x-ui requires an admin base URI and bearer token when enabled");
            }
            if (connectTimeout == null || connectTimeout.isZero() || connectTimeout.isNegative()
                    || readTimeout == null || readTimeout.isZero() || readTimeout.isNegative()) {
                throw new IllegalArgumentException("3x-ui HTTP timeouts must be positive");
            }
            String scheme = adminBaseUri.getScheme();
            boolean localHttp = "http".equalsIgnoreCase(scheme)
                    && ("localhost".equalsIgnoreCase(adminBaseUri.getHost())
                    || "127.0.0.1".equals(adminBaseUri.getHost())
                    || "::1".equals(adminBaseUri.getHost()));
            if (!"https".equalsIgnoreCase(scheme) && !localHttp) {
                throw new IllegalArgumentException("3x-ui admin URI must use HTTPS (HTTP is allowed for localhost)");
            }
            if (adminBaseUri.getHost() == null || adminBaseUri.getUserInfo() != null
                    || adminBaseUri.getQuery() != null || adminBaseUri.getFragment() != null) {
                throw new IllegalArgumentException("3x-ui admin URI must be an origin and optional base path");
            }
            String normalized = adminBaseUri.toString();
            if (!normalized.endsWith("/")) {
                adminBaseUri = URI.create(normalized + "/");
            }
        }
    }
}
