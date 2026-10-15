package com.openviya.backend.subscriptions;

public class SubscriptionActivationException extends RuntimeException {

    public SubscriptionActivationException(String code) {
        super(code);
    }
}
