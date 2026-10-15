package com.openviya.backend.identity;

import java.math.BigDecimal;
import java.math.BigInteger;

public record TelegramProfile(
        String telegramUserId,
        String username,
        String displayName) {

    private static final BigInteger MAX_TELEGRAM_ID = BigInteger.ONE.shiftLeft(52).subtract(BigInteger.ONE);

    public TelegramProfile {
        if (!isValidTelegramUserId(telegramUserId)) {
            throw new IllegalArgumentException("Telegram user id must be a positive integer with at most 52 bits");
        }
        if (username != null && username.length() > 255) {
            throw new IllegalArgumentException("Telegram username is too long");
        }
        if (displayName != null && displayName.length() > 255) {
            throw new IllegalArgumentException("Telegram display name is too long");
        }
    }

    public static String numericUserIdFromOidcClaim(Object claim) {
        if (!(claim instanceof Number number)) {
            throw new IllegalArgumentException("Telegram OIDC profile.id must be a JSON number");
        }
        try {
            BigInteger id = new BigDecimal(number.toString()).toBigIntegerExact();
            if (id.signum() <= 0 || id.compareTo(MAX_TELEGRAM_ID) > 0) {
                throw new IllegalArgumentException("Telegram OIDC profile.id is outside the supported range");
            }
            return id.toString();
        } catch (ArithmeticException | NumberFormatException exception) {
            throw new IllegalArgumentException("Telegram OIDC profile.id must be an exact integer", exception);
        }
    }

    private static boolean isValidTelegramUserId(String value) {
        if (value == null || !value.matches("[1-9][0-9]{0,19}")) {
            return false;
        }
        return new BigInteger(value).compareTo(MAX_TELEGRAM_ID) <= 0;
    }
}
