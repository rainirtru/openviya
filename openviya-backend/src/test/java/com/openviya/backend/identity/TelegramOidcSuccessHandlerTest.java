package com.openviya.backend.identity;

import static org.junit.jupiter.api.Assertions.*;
import static org.mockito.ArgumentMatchers.*;
import static org.mockito.Mockito.*;

import com.openviya.backend.audit.AuditEventRepository;
import java.time.Instant;
import java.util.HashMap;
import java.util.UUID;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.mockito.ArgumentCaptor;
import org.springframework.mock.web.MockHttpServletRequest;
import org.springframework.mock.web.MockHttpServletResponse;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.context.SecurityContext;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.security.oauth2.core.oidc.OidcIdToken;
import org.springframework.security.oauth2.core.oidc.user.OidcUser;
import org.springframework.security.web.context.SecurityContextRepository;

class TelegramOidcSuccessHandlerTest {
    private final UserIdentityRepository identities = mock(UserIdentityRepository.class);
    private final AuditEventRepository audit = mock(AuditEventRepository.class);
    private final SecurityContextRepository contexts = mock(SecurityContextRepository.class);
    private final TelegramOidcSuccessHandler handler = new TelegramOidcSuccessHandler(identities, audit, contexts, "https://openviya.online");

    @AfterEach
    void clearContext() { SecurityContextHolder.clearContext(); }

    @Test
    void linksStringIdToInternalIdentityAndCreatesUserSession() throws Exception {
        UUID userId = UUID.randomUUID();
        when(identities.resolveTelegramIdentity(any(), any())).thenReturn(new ExternalIdentity(userId,
                ExternalIdentity.Provider.TELEGRAM, "712345678", "test_user", "Test User", Instant.now()));
        var request = new MockHttpServletRequest();
        var response = new MockHttpServletResponse();
        handler.onAuthenticationSuccess(request, response, authentication("712345678"));
        var profile = ArgumentCaptor.forClass(TelegramProfile.class);
        verify(identities).resolveTelegramIdentity(profile.capture(), any());
        assertEquals("712345678", profile.getValue().telegramUserId());
        var saved = ArgumentCaptor.forClass(SecurityContext.class);
        verify(contexts).saveContext(saved.capture(), same(request), same(response));
        var principal = (UserPrincipal) saved.getValue().getAuthentication().getPrincipal();
        assertEquals(userId, principal.userId());
        assertEquals("712345678", principal.telegramUserId());
        assertTrue(saved.getValue().getAuthentication().getAuthorities().stream().anyMatch(a -> a.getAuthority().equals("ROLE_USER")));
        assertEquals(303, response.getStatus());
        assertEquals("https://openviya.online", response.getHeader("Location"));
    }

    @Test
    void doesNotUseOidcSubjectWhenProfileIdIsMissing() throws Exception {
        var response = new MockHttpServletResponse();
        handler.onAuthenticationSuccess(new MockHttpServletRequest(), response, authentication(null));
        assertEquals(403, response.getStatus());
        assertEquals("{\"code\":\"TELEGRAM_ACCOUNT_UNAVAILABLE\"}", response.getContentAsString());
        verifyNoInteractions(identities, contexts, audit);
    }

    @Test
    void stringRepresentationDoesNotBypassSuspendedAccountChecks() throws Exception {
        when(identities.resolveTelegramIdentity(any(), any())).thenThrow(new UserIdentityException("USER_ACCOUNT_UNAVAILABLE"));
        var request = new MockHttpServletRequest();
        request.getSession(true);
        var response = new MockHttpServletResponse();
        handler.onAuthenticationSuccess(request, response, authentication("712345678"));
        assertEquals(403, response.getStatus());
        assertNull(request.getSession(false));
        verifyNoInteractions(contexts, audit);
    }

    private Authentication authentication(Object id) {
        var claims = new HashMap<String,Object>();
        claims.put("sub", "different-oidc-subject");
        claims.put("preferred_username", "test_user");
        claims.put("name", "Test User");
        if (id != null) claims.put("id", id);
        var token = new OidcIdToken("test-token", Instant.now(), Instant.now().plusSeconds(300), claims);
        var user = mock(OidcUser.class);
        when(user.getIdToken()).thenReturn(token);
        var authentication = mock(Authentication.class);
        when(authentication.getPrincipal()).thenReturn(user);
        return authentication;
    }
}
