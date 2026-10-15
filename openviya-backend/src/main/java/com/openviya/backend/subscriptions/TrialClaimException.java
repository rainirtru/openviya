package com.openviya.backend.subscriptions;

import org.springframework.http.HttpStatus;

public class TrialClaimException extends RuntimeException {

    private final HttpStatus status;
    private final String code;

    public TrialClaimException(HttpStatus status, String code) {
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
