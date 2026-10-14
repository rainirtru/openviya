package com.openviya.backend.security;

import org.springframework.http.HttpStatus;

public class StaffAuthException extends RuntimeException {

    private final HttpStatus status;
    private final String code;

    public StaffAuthException(HttpStatus status, String code) {
        super(code);
        this.status = status;
        this.code = code;
    }

    public HttpStatus status() {
        return status;
    }

    public String code() {
        return code;
    }
}
