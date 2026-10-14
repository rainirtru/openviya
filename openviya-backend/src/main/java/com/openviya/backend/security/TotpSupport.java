package com.openviya.backend.security;

import com.eatthepath.otp.TimeBasedOneTimePasswordGenerator;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.InvalidKeyException;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.time.Instant;
import java.util.UUID;
import javax.crypto.spec.SecretKeySpec;
import org.springframework.stereotype.Component;

@Component
public class TotpSupport {

    private static final long STEP_SECONDS = 30;
    private final TimeBasedOneTimePasswordGenerator generator = createGenerator();

    private static TimeBasedOneTimePasswordGenerator createGenerator() {
        try {
            return new TimeBasedOneTimePasswordGenerator(Duration.ofSeconds(STEP_SECONDS), 6, "HmacSHA1");
        } catch (NoSuchAlgorithmException exception) {
            throw new ExceptionInInitializerError(exception);
        }
    }

    public byte[] newSecret() {
        byte[] secret = new byte[20];
        new java.security.SecureRandom().nextBytes(secret);
        return secret;
    }

    public String toBase32(byte[] secret) {
        return Base32.encode(secret);
    }

    public String provisioningUri(String login, byte[] secret) {
        String issuer = "OpenViya";
        String label = URLEncoder.encode(issuer + ":" + login, StandardCharsets.UTF_8).replace("+", "%20");
        String issuerParam = URLEncoder.encode(issuer, StandardCharsets.UTF_8);
        return "otpauth://totp/" + label + "?secret=" + toBase32(secret)
                + "&issuer=" + issuerParam + "&algorithm=SHA1&digits=6&period=" + STEP_SECONDS;
    }

    public Long matchingStep(byte[] secret, String submittedCode, Instant now, Long lastAcceptedStep) {
        if (submittedCode == null || !submittedCode.matches("[0-9]{6}")) {
            return null;
        }
        var key = new SecretKeySpec(secret, "HmacSHA1");
        long currentStep = Math.floorDiv(now.getEpochSecond(), STEP_SECONDS);
        for (long step = currentStep - 1; step <= currentStep + 1; step++) {
            if (step < 0 || (lastAcceptedStep != null && step <= lastAcceptedStep)) {
                continue;
            }
            try {
                String expected = generator.generateOneTimePasswordString(key, Instant.ofEpochSecond(step * STEP_SECONDS));
                if (java.security.MessageDigest.isEqual(
                        expected.getBytes(StandardCharsets.US_ASCII),
                        submittedCode.getBytes(StandardCharsets.US_ASCII))) {
                    return step;
                }
            } catch (InvalidKeyException exception) {
                throw new IllegalStateException("Unable to verify TOTP code", exception);
            }
        }
        return null;
    }
}
