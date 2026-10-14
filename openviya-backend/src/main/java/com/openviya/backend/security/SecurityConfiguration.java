package com.openviya.backend.security;

import com.openviya.backend.identity.TelegramOidcSuccessHandler;
import org.springframework.beans.factory.ObjectProvider;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.HttpMethod;
import org.springframework.boot.web.servlet.FilterRegistrationBean;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.config.annotation.web.configuration.EnableWebSecurity;
import org.springframework.security.config.annotation.web.configurers.AbstractHttpConfigurer;
import org.springframework.security.core.userdetails.UserDetailsService;
import org.springframework.security.core.userdetails.UsernameNotFoundException;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.oauth2.client.web.OAuth2AuthorizedClientRepository;
import org.springframework.security.web.context.HttpSessionSecurityContextRepository;
import org.springframework.security.web.context.SecurityContextRepository;
import org.springframework.security.web.SecurityFilterChain;
import org.springframework.security.web.access.intercept.AuthorizationFilter;

@Configuration
@EnableWebSecurity
public class SecurityConfiguration {

    @Bean
    UserDetailsService noPasswordAuthenticationProvider() {
        return username -> {
            throw new UsernameNotFoundException("Staff login is handled by the two-factor challenge flow");
        };
    }

    @Bean
    FilterRegistrationBean<StaffSessionValidationFilter> staffSessionValidationFilterRegistration(
            StaffSessionValidationFilter filter) {
        FilterRegistrationBean<StaffSessionValidationFilter> registration = new FilterRegistrationBean<>(filter);
        registration.setEnabled(false);
        return registration;
    }

    @Bean
    SecurityContextRepository securityContextRepository() {
        return new HttpSessionSecurityContextRepository();
    }

    @Bean
    SecurityFilterChain applicationSecurity(
            HttpSecurity http,
            SecurityContextRepository contextRepository,
            StaffSessionValidationFilter staffSessionValidationFilter,
            ObjectProvider<ClientRegistrationRepository> clientRegistrations,
            ObjectProvider<OAuth2AuthorizedClientRepository> telegramAuthorizedClientRepository,
            TelegramOidcSuccessHandler telegramSuccessHandler) throws Exception {
        http
                .csrf(csrf -> {})
                .securityContext(context -> context.securityContextRepository(contextRepository))
                .formLogin(AbstractHttpConfigurer::disable)
                .httpBasic(AbstractHttpConfigurer::disable)
                .logout(AbstractHttpConfigurer::disable)
                .authorizeHttpRequests(authorize -> authorize
                        .requestMatchers(HttpMethod.GET, "/actuator/health/**", "/actuator/info", "/api/v1").permitAll()
                        .requestMatchers("/api/v1/staff/auth/**").permitAll()
                        .requestMatchers("/api/v1/auth/telegram/**", "/api/v1/auth/logout",
                                "/oauth2/**", "/login/oauth2/**").permitAll()
                        .requestMatchers("/api/v1/me", "/api/v1/me/**").hasRole("USER")
                        .requestMatchers("/api/v1/admin/owner/**").hasRole("OWNER")
                        .requestMatchers("/api/v1/admin/staff/**").hasRole("OWNER")
                        .requestMatchers("/api/v1/admin/**").hasAnyRole("OWNER", "ADMIN")
                        .requestMatchers("/api/v1/support/**").hasAnyRole("OWNER", "ADMIN", "SUPPORT")
                        .anyRequest().denyAll())
                .addFilterBefore(staffSessionValidationFilter, AuthorizationFilter.class);
        if (clientRegistrations.getIfAvailable() != null) {
            http.oauth2Login(oauth2 -> oauth2
                    .authorizedClientRepository(telegramAuthorizedClientRepository.getObject())
                    .successHandler(telegramSuccessHandler));
        }
        return http.build();
    }
}
