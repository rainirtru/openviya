package com.openviya.backend.integrations.threexui;

public class ThreeXUiApiException extends RuntimeException {

    public ThreeXUiApiException(String message) {
        super(message);
    }

    public ThreeXUiApiException(String message, Throwable cause) {
        super(message, cause);
    }
}
