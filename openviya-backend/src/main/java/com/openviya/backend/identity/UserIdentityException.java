package com.openviya.backend.identity;

public class UserIdentityException extends RuntimeException {

    private final String code;

    public UserIdentityException(String code) {
        super(code);
        this.code = code;
    }

    public String code() {
        return code;
    }
}
