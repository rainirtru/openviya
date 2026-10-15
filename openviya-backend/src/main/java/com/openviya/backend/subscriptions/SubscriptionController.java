package com.openviya.backend.subscriptions;

import com.openviya.backend.identity.UserPrincipal;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class SubscriptionController {

    private final SubscriptionReadService subscriptions;

    public SubscriptionController(SubscriptionReadService subscriptions) {
        this.subscriptions = subscriptions;
    }

    @GetMapping("/api/v1/subscription")
    public SubscriptionSnapshot get(@AuthenticationPrincipal UserPrincipal principal) {
        return subscriptions.getForUser(principal.userId());
    }
}
