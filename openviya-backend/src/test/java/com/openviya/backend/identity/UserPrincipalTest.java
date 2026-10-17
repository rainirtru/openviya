package com.openviya.backend.identity;

import static org.junit.jupiter.api.Assertions.*;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.authority.SimpleGrantedAuthority;

class UserPrincipalTest {
    @Test
    void authenticationNameIsStableInternalIdInsteadOfProfileText() {
        UUID id = UUID.randomUUID();
        var principal = new UserPrincipal(id, "712345678", "test_user", "Long display name ".repeat(12));
        assertTrue(principal.toString().length() > 100);
        var authentication = UsernamePasswordAuthenticationToken.authenticated(principal, null,
                List.of(new SimpleGrantedAuthority("ROLE_USER")));
        assertEquals(id.toString(), authentication.getName());
        assertEquals(36, authentication.getName().length());
        assertEquals(authentication.getName(), new UserPrincipal(id, "712345678", "renamed", "New name").getName());
    }
}
