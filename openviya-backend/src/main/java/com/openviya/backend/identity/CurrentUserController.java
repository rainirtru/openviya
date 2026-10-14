package com.openviya.backend.identity;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1/me")
public class CurrentUserController {

    @GetMapping
    public UserProfile currentUser(@AuthenticationPrincipal UserPrincipal principal) {
        return new UserProfile(principal.userId(), principal.telegramUserId(), principal.username(), principal.displayName());
    }

    public record UserProfile(java.util.UUID userId, String telegramUserId, String username, String displayName) {
    }
}
