package com.openviya.backend.security;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import jakarta.servlet.http.HttpSession;
import jakarta.validation.Valid;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Pattern;
import java.util.List;
import java.util.Set;
import java.util.UUID;
import java.util.stream.Collectors;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.authority.SimpleGrantedAuthority;
import org.springframework.security.core.context.SecurityContext;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.security.web.context.SecurityContextRepository;
import org.springframework.security.web.csrf.CsrfToken;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1/staff/auth")
public class StaffAuthController {

    private final StaffAuthenticationService authenticationService;
    private final SecurityContextRepository contextRepository;

    public StaffAuthController(
            StaffAuthenticationService authenticationService,
            SecurityContextRepository contextRepository) {
        this.authenticationService = authenticationService;
        this.contextRepository = contextRepository;
    }

    @GetMapping("/csrf")
    public CsrfResponse csrf(HttpServletRequest request) {
        Object token = request.getAttribute(CsrfToken.class.getName());
        if (!(token instanceof CsrfToken)) {
            token = request.getAttribute("_csrf");
            if (!(token instanceof CsrfToken)) {
                throw new IllegalStateException("CSRF token is unavailable");
            }
        }
        CsrfToken csrfToken = (CsrfToken) token;
        return new CsrfResponse(csrfToken.getHeaderName(), csrfToken.getToken());
    }

    @PostMapping("/login")
    public StaffAuthenticationService.PasswordChallenge login(
            @Valid @RequestBody PasswordRequest request,
            HttpServletRequest servletRequest) {
        return authenticationService.beginPasswordLogin(
                request.login(), request.password(), servletRequest.getRemoteAddr());
    }

    @PostMapping("/totp/enrollment")
    public StaffAuthenticationService.TotpEnrollment enrollTotp(
            @Valid @RequestBody ChallengeRequest request,
            HttpServletRequest servletRequest) {
        return authenticationService.beginTotpEnrollment(request.challengeId(), servletRequest.getRemoteAddr());
    }

    @PostMapping("/totp/verify")
    public ResponseEntity<?> verifyTotp(
            @Valid @RequestBody TotpRequest request,
            HttpServletRequest servletRequest,
            HttpServletResponse servletResponse) {
        StaffAuthenticationService.Completion completion = authenticationService.completeTwoFactor(
                request.challengeId(), request.code(), servletRequest.getRemoteAddr(),
                (String) servletRequest.getAttribute(com.openviya.backend.api.CorrelationIdFilter.ATTRIBUTE_NAME));
        return switch (completion.status()) {
            case "SUCCESS" -> {
                Authentication authentication = authentication(completion.principal());
                servletRequest.getSession(true);
                servletRequest.changeSessionId();
                SecurityContext context = SecurityContextHolder.createEmptyContext();
                context.setAuthentication(authentication);
                SecurityContextHolder.setContext(context);
                contextRepository.saveContext(context, servletRequest, servletResponse);
                yield ResponseEntity.ok(new LoginResponse(
                        completion.principal().staffId(), completion.principal().login(),
                        completion.principal().roles(), completion.recoveryCodes()));
            }
            case "RATE_LIMITED" -> ResponseEntity.status(HttpStatus.TOO_MANY_REQUESTS)
                    .body(new AuthFailure("AUTH_RATE_LIMITED"));
            case "ENROLLMENT_REQUIRED" -> ResponseEntity.status(HttpStatus.CONFLICT)
                    .body(new AuthFailure("TOTP_ENROLLMENT_REQUIRED"));
            case "INVALID_CODE", "INVALID_CHALLENGE" -> ResponseEntity.status(HttpStatus.UNAUTHORIZED)
                    .body(new AuthFailure("INVALID_CHALLENGE_OR_CODE"));
            default -> ResponseEntity.status(HttpStatus.UNAUTHORIZED).body(new AuthFailure("INVALID_CHALLENGE_OR_CODE"));
        };
    }

    @PostMapping("/logout")
    public ResponseEntity<Void> logout(HttpServletRequest request) {
        HttpSession session = request.getSession(false);
        if (session != null) {
            session.invalidate();
        }
        SecurityContextHolder.clearContext();
        return ResponseEntity.noContent().build();
    }

    private static Authentication authentication(StaffPrincipal principal) {
        Set<SimpleGrantedAuthority> authorities = principal.roles().stream()
                .map(role -> new SimpleGrantedAuthority("ROLE_" + role))
                .collect(Collectors.toUnmodifiableSet());
        return UsernamePasswordAuthenticationToken.authenticated(principal, null, authorities);
    }

    public record PasswordRequest(@NotBlank @Pattern(regexp = "[^\\r\\n]{1,120}") String login,
            @NotBlank @Pattern(regexp = "[^\\r\\n]{1,256}") String password) {
    }

    public record ChallengeRequest(@NotNull UUID challengeId) {
    }

    public record TotpRequest(@NotNull UUID challengeId,
            @NotBlank @Pattern(regexp = "[A-Za-z0-9 -]{6,40}") String code) {
    }

    public record CsrfResponse(String headerName, String token) {
    }

    public record LoginResponse(UUID staffId, String login, Set<String> roles, List<String> recoveryCodes) {
    }

    public record AuthFailure(String code) {
    }
}
