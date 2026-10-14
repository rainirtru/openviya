package com.openviya.backend.security;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.UUID;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;

@Component
public class TotpSecretCipher {

    private static final int NONCE_LENGTH = 12;
    private static final int TAG_LENGTH_BITS = 128;
    private final String encodedKey;
    private final String keyVersion;
    private final SecureRandom secureRandom = new SecureRandom();

    public TotpSecretCipher(
            @Value("${openviya.auth.totp-encryption-key:}") String encodedKey,
            @Value("${openviya.auth.totp-encryption-key-version:v1}") String keyVersion) {
        this.encodedKey = encodedKey;
        this.keyVersion = keyVersion;
    }

    public EncryptedSecret encrypt(UUID staffId, byte[] secret) {
        try {
            byte[] nonce = new byte[NONCE_LENGTH];
            secureRandom.nextBytes(nonce);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, encryptionKey(), new GCMParameterSpec(TAG_LENGTH_BITS, nonce));
            cipher.updateAAD(staffId.toString().getBytes(StandardCharsets.UTF_8));
            byte[] ciphertext = cipher.doFinal(secret);
            return new EncryptedSecret(ByteBuffer.allocate(nonce.length + ciphertext.length)
                    .put(nonce).put(ciphertext).array(), keyVersion);
        } catch (GeneralSecurityException exception) {
            throw new IllegalStateException("Unable to protect TOTP secret", exception);
        }
    }

    public byte[] decrypt(UUID staffId, byte[] encrypted, String storedKeyVersion) {
        if (!keyVersion.equals(storedKeyVersion)) {
            throw new IllegalStateException("TOTP encryption key version is unavailable");
        }
        if (encrypted == null || encrypted.length <= NONCE_LENGTH) {
            throw new IllegalStateException("Stored TOTP secret is invalid");
        }
        try {
            ByteBuffer payload = ByteBuffer.wrap(encrypted);
            byte[] nonce = new byte[NONCE_LENGTH];
            payload.get(nonce);
            byte[] ciphertext = new byte[payload.remaining()];
            payload.get(ciphertext);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, encryptionKey(), new GCMParameterSpec(TAG_LENGTH_BITS, nonce));
            cipher.updateAAD(staffId.toString().getBytes(StandardCharsets.UTF_8));
            return cipher.doFinal(ciphertext);
        } catch (GeneralSecurityException exception) {
            throw new IllegalStateException("Unable to decrypt TOTP secret", exception);
        }
    }

    private SecretKeySpec encryptionKey() {
        if (encodedKey == null || encodedKey.isBlank()) {
            throw new IllegalStateException("OPENVIYA_TOTP_ENCRYPTION_KEY must be configured before staff 2FA is used");
        }
        byte[] key;
        try {
            key = Base64.getDecoder().decode(encodedKey);
        } catch (IllegalArgumentException exception) {
            throw new IllegalStateException("TOTP encryption key must be base64 encoded", exception);
        }
        if (key.length != 32) {
            throw new IllegalStateException("TOTP encryption key must contain 32 bytes");
        }
        return new SecretKeySpec(key, "AES");
    }

    public record EncryptedSecret(byte[] ciphertext, String keyVersion) {
    }
}
