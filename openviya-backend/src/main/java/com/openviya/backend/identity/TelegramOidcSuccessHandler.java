package com.openviya.backend.identity;

import com.openviya.backend.api.CorrelationIdFilter;
import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import jakarta.servlet.http.HttpSession;
import java.io.IOException;
import java.time.Instant;
import java.util.Set;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.http.MediaType;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.authority.SimpleGrantedAuthority;
import org.springframework.security.core.context.SecurityContext;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.security.oauth2.core.oidc.user.OidcUser;
import org.springframework.security.web.authentication.AuthenticationSuccessHandler;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.web.context.SecurityContextRepository;
import org.springframework.stereotype.Component;

@Component
public class TelegramOidcSuccessHandler implements AuthenticationSuccessHandler {

    private final UserIdentityRepository identities;
    private final AuditEventRepository auditEvents;
    private final SecurityContextRepository contextRepository;
    private final String publicBaseUrl;

    public TelegramOidcSuccessHandler(
            UserIdentityRepository identities,
            AuditEventRepository auditEvents,
            SecurityContextRepository contextRepository,
            @Value("${openviya.public-base-url}") String publicBaseUrl) {
        this.identities = identities;
        this.auditEvents = auditEvents;
        this.contextRepository = contextRepository;
        this.publicBaseUrl = publicBaseUrl;
    }

    @Override
    public void onAuthenticationSuccess(
            HttpServletRequest request, HttpServletResponse response, Authentication authentication)
            throws IOException, ServletException {
        if (!(authentication.getPrincipal() instanceof OidcUser oidcUser)) {
            SecurityContextHolder.clearContext();
            fail(response, HttpServletResponse.SC_UNAUTHORIZED, "TELEGRAM_LOGIN_FAILED");
            return;
        }

        try {
            var claims = oidcUser.getIdToken().getClaims();
            String telegramId = TelegramProfile.numericUserIdFromOidcClaim(claims.get("id"));
            String username = stringClaim(claims.get("preferred_username"));
            String displayName = stringClaim(claims.get("name"));
            if (displayName == null) {
                displayName = String.join(" ",
                        nullToEmpty(stringClaim(claims.get("given_name"))),
                        nullToEmpty(stringClaim(claims.get("family_name")))).strip();
                if (displayName.isEmpty()) {
                    displayName = null;
                }
            }

            TelegramProfile profile = new TelegramProfile(telegramId, username, displayName);
            ExternalIdentity identity = identities.resolveTelegramIdentity(profile, Instant.now());
            UserPrincipal principal = new UserPrincipal(
                    identity.userId(), identity.providerSubject(), identity.username(), identity.displayName());
            var authorities = Set.of(new SimpleGrantedAuthority("ROLE_USER"));
            var userAuthentication = UsernamePasswordAuthenticationToken.authenticated(principal, null, authorities);

            request.getSession(true);
            request.changeSessionId();
            SecurityContext context = SecurityContextHolder.createEmptyContext();
            context.setAuthentication(userAuthentication);
            SecurityContextHolder.setContext(context);
            contextRepository.saveContext(context, request, response);
            auditEvents.append(new AuditActor(AuditActor.Kind.USER, identity.userId()),
                    "user.telegram.login", "USER", identity.userId().toString(), null, null, null,
                    correlationId(request));

            response.setStatus(HttpServletResponse.SC_SEE_OTHER);
            response.setHeader("Location", publicBaseUrl);
        } catch (UserIdentityException | IllegalArgumentException exception) {
            SecurityContextHolder.clearContext();
            HttpSession session = request.getSession(false);
            if (session != null) {
                session.invalidate();
            }
            fail(response, HttpServletResponse.SC_FORBIDDEN, "TELEGRAM_ACCOUNT_UNAVAILABLE");
        }
    }

    private static String stringClaim(Object value) {
        if (value instanceof String string && !string.isBlank()) {
            int codePoints = string.codePointCount(0, string.length());
            return codePoints <= 255 ? string : string.substring(0, string.offsetByCodePoints(0, 255));
        }
        return null;
    }

    private static String nullToEmpty(String value) {
        return value == null ? "" : value;
    }

    private static String correlationId(HttpServletRequest request) {
        Object value = request.getAttribute(CorrelationIdFilter.ATTRIBUTE_NAME);
        return value instanceof String string ? string : null;
    }

    private static void fail(HttpServletResponse response, int status, String code) throws IOException {
        response.setStatus(status);
        response.setContentType(MediaType.APPLICATION_JSON_VALUE);
        response.getWriter().write("{\"code\":\"" + code + "\"}");
    }
}
