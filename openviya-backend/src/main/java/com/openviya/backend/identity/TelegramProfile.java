package com.openviya.backend.identity;

public record TelegramProfile(
        String telegramUserId,
        String username,
        String displayName) {

    public TelegramProfile {
        if (telegramUserId == null || !telegramUserId.matches("[1-9][0-9]{0,19}")) {
            throw new IllegalArgumentException("Telegram user id must be a positive decimal identifier");
        }
        if (username != null && username.length() > 255) {
            throw new IllegalArgumentException("Telegram username is too long");
        }
        if (displayName != null && displayName.length() > 255) {
            throw new IllegalArgumentException("Telegram display name is too long");
        }
    }
}
