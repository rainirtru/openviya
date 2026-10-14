package com.openviya.backend.identity;

import com.openviya.backend.api.CorrelationIdFilter;
import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import com.openviya.backend.security.AuthRateLimitService;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Pattern;
import jakarta.validation.constraints.Size;
import java.time.Duration;
import java.time.Instant;
import java.util.UUID;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1/internal/telegram/identity")
public class TelegramBotIdentityController {

    private static final Duration RATE_WINDOW = Duration.ofMinutes(15);
    private static final Duration RATE_BLOCK = Duration.ofMinutes(15);
    private final TelegramBotServiceAuthenticator authenticator;
    private final AuthRateLimitService rateLimits;
    private final UserIdentityRepository identities;
    private final AuditEventRepository auditEvents;

    public TelegramBotIdentityController(
            TelegramBotServiceAuthenticator authenticator,
            AuthRateLimitService rateLimits,
            UserIdentityRepository identities,
            AuditEventRepository auditEvents) {
        this.authenticator = authenticator;
        this.rateLimits = rateLimits;
        this.identities = identities;
        this.auditEvents = auditEvents;
    }

    @PostMapping("/resolve")
    public ResponseEntity<?> resolve(
            @Valid @RequestBody ResolveRequest request,
            @RequestHeader(name = "Authorization", required = false) String authorization,
            HttpServletRequest servletRequest) {
        String source = servletRequest.getRemoteAddr();
        if (!rateLimits.allow("telegram-bot-auth", "telegram-bot", source,
                30, RATE_WINDOW, RATE_BLOCK)) {
            return ResponseEntity.status(HttpStatus.TOO_MANY_REQUESTS).body(new Failure("AUTH_RATE_LIMITED"));
        }
        if (!authenticator.configured()) {
            return ResponseEntity.status(HttpStatus.SERVICE_UNAVAILABLE)
                    .body(new Failure("BOT_SERVICE_AUTH_NOT_CONFIGURED"));
        }
        if (!authenticator.matchesBearerHeader(authorization)) {
            return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body(new Failure("UNAUTHORIZED"));
        }
        if (!rateLimits.allow("telegram-bot-identity", request.telegramUserId(), source,
                30, RATE_WINDOW, RATE_BLOCK)) {
            return ResponseEntity.status(HttpStatus.TOO_MANY_REQUESTS).body(new Failure("AUTH_RATE_LIMITED"));
        }

        try {
            var identity = identities.resolveTelegramIdentity(
                    new TelegramProfile(request.telegramUserId(), request.username(), request.displayName()),
                    Instant.now());
            auditEvents.append(new AuditActor(AuditActor.Kind.SYSTEM, null),
                    "user.telegram.bot_identity_resolved", "USER", identity.userId().toString(),
                    "Trusted Telegram bot resolved a Telegram identity", null, null, correlationId(servletRequest));
            return ResponseEntity.ok(new ResolvedIdentity(identity.userId()));
        } catch (UserIdentityException exception) {
            return ResponseEntity.status(HttpStatus.FORBIDDEN).body(new Failure(exception.code()));
        }
    }

    private static String correlationId(HttpServletRequest request) {
        Object value = request.getAttribute(CorrelationIdFilter.ATTRIBUTE_NAME);
        return value instanceof String string ? string : null;
    }

    public record ResolveRequest(
            @NotBlank @Pattern(regexp = "[1-9][0-9]{0,19}") String telegramUserId,
            @Size(max = 255) String username,
            @Size(max = 255) String displayName) {
    }

    public record ResolvedIdentity(UUID userId) {
    }

    public record Failure(String code) {
    }
}
