package com.openviya.backend.security;

import com.openviya.backend.identity.UserIdentityRepository;
import com.openviya.backend.identity.UserPrincipal;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import java.io.IOException;
import java.util.Set;
import java.util.stream.Collectors;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;

@Component
public class StaffSessionValidationFilter extends OncePerRequestFilter {

    private final StaffAuthRepository repository;
    private final UserIdentityRepository userIdentities;

    public StaffSessionValidationFilter(StaffAuthRepository repository, UserIdentityRepository userIdentities) {
        this.repository = repository;
        this.userIdentities = userIdentities;
    }

    @Override
    protected void doFilterInternal(
            HttpServletRequest request,
            HttpServletResponse response,
            FilterChain filterChain) throws ServletException, IOException {
        var authentication = SecurityContextHolder.getContext().getAuthentication();
        if (authentication != null && authentication.isAuthenticated()
                && authentication.getPrincipal() instanceof StaffPrincipal principal) {
            StaffAuthRepository.CurrentStaff current = repository.currentStaff(principal.staffId());
            if (current == null || !current.status().equals("ACTIVE")
                    || current.authVersion() != principal.authVersion()) {
                var session = request.getSession(false);
                if (session != null) {
                    session.invalidate();
                }
                SecurityContextHolder.clearContext();
            } else {
                Set<String> roles = Set.copyOf(repository.roles(principal.staffId()));
                var refreshedPrincipal = new StaffPrincipal(
                        current.id(), current.login(), current.authVersion(), roles);
                var authorities = roles.stream().map(role -> new org.springframework.security.core.authority
                        .SimpleGrantedAuthority("ROLE_" + role)).collect(Collectors.toUnmodifiableSet());
                SecurityContextHolder.getContext().setAuthentication(
                        UsernamePasswordAuthenticationToken.authenticated(refreshedPrincipal, null, authorities));
            }
        } else if (authentication != null && authentication.isAuthenticated()
                && authentication.getPrincipal() instanceof UserPrincipal principal
                && !userIdentities.isActive(principal.userId())) {
            var session = request.getSession(false);
            if (session != null) {
                session.invalidate();
            }
            SecurityContextHolder.clearContext();
        }
        filterChain.doFilter(request, response);
    }
}
