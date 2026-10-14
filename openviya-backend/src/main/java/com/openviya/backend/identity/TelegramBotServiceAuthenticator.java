package com.openviya.backend.identity;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;

@Component
public class TelegramBotServiceAuthenticator {

    private final byte[] expectedHash;

    public TelegramBotServiceAuthenticator(@Value("${openviya.auth.telegram.bot-service-token:}") String configuredToken) {
        if (configuredToken == null || configuredToken.isBlank()) {
            expectedHash = null;
            return;
        }
        if (configuredToken.length() < 32) {
            throw new IllegalStateException("Telegram bot service token must contain at least 32 characters");
        }
        expectedHash = sha256(configuredToken);
    }

    public boolean configured() {
        return expectedHash != null;
    }

    public boolean matchesBearerHeader(String authorizationHeader) {
        if (expectedHash == null) {
            return false;
        }
        String token = authorizationHeader != null && authorizationHeader.startsWith("Bearer ")
                ? authorizationHeader.substring("Bearer ".length())
                : "";
        return MessageDigest.isEqual(expectedHash, sha256(token));
    }

    private static byte[] sha256(String token) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(token.getBytes(StandardCharsets.UTF_8));
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 is unavailable", exception);
        }
    }
}
