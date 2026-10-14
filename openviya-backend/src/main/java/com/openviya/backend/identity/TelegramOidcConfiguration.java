package com.openviya.backend.identity;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.oauth2.client.registration.InMemoryClientRegistrationRepository;
import org.springframework.security.oauth2.client.web.OAuth2AuthorizedClientRepository;

@Configuration
@ConditionalOnProperty(name = "openviya.auth.telegram.enabled", havingValue = "true")
public class TelegramOidcConfiguration {

    private static final String ISSUER = "https://oauth.telegram.org";
    private static final String AUTHORIZATION_ENDPOINT = ISSUER + "/auth";
    private static final String TOKEN_ENDPOINT = ISSUER + "/token";
    private static final String JWKS_ENDPOINT = ISSUER + "/.well-known/jwks.json";

    @Bean
    OAuth2AuthorizedClientRepository telegramAuthorizedClientRepository() {
        return new NoStoreOAuth2AuthorizedClientRepository();
    }

    @Bean
    ClientRegistrationRepository telegramClientRegistrationRepository(
            @Value("${openviya.auth.telegram.client-id:}") String clientId,
            @Value("${openviya.auth.telegram.client-secret:}") String clientSecret,
            @Value("${openviya.auth.telegram.redirect-uri:}") String configuredRedirectUri) {
        if (clientId == null || clientId.isBlank() || clientSecret == null || clientSecret.isBlank()) {
            throw new IllegalStateException("Telegram OIDC requires a BotFather client ID and client secret");
        }
        String redirectUri = configuredRedirectUri == null || configuredRedirectUri.isBlank()
                ? "{baseUrl}/login/oauth2/code/{registrationId}"
                : configuredRedirectUri.strip();
        ClientRegistration telegram = ClientRegistration.withRegistrationId("telegram")
                .clientId(clientId.strip())
                .clientSecret(clientSecret)
                .clientAuthenticationMethod(org.springframework.security.oauth2.core.ClientAuthenticationMethod.CLIENT_SECRET_BASIC)
                .authorizationGrantType(org.springframework.security.oauth2.core.AuthorizationGrantType.AUTHORIZATION_CODE)
                .redirectUri(redirectUri)
                .scope("openid", "profile")
                .authorizationUri(AUTHORIZATION_ENDPOINT)
                .tokenUri(TOKEN_ENDPOINT)
                .jwkSetUri(JWKS_ENDPOINT)
                .userInfoUri("")
                .userNameAttributeName("sub")
                .clientName("Telegram")
                .issuerUri(ISSUER)
                .clientSettings(ClientRegistration.ClientSettings.builder().requireProofKey(true).build())
                .build();
        return new InMemoryClientRegistrationRepository(telegram);
    }

}
