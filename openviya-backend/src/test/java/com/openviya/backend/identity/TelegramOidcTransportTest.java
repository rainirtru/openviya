package com.openviya.backend.identity;

import static org.junit.jupiter.api.Assertions.*;

import com.nimbusds.jose.JOSEException;
import com.nimbusds.jose.JWSAlgorithm;
import com.nimbusds.jose.JWSHeader;
import com.nimbusds.jose.crypto.RSASSASigner;
import com.nimbusds.jose.jwk.JWKSet;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jose.jwk.gen.RSAKeyGenerator;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.SignedJWT;
import com.sun.net.httpserver.HttpServer;
import java.io.ByteArrayOutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.Date;
import java.util.zip.GZIPOutputStream;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.security.oauth2.client.endpoint.OAuth2AuthorizationCodeGrantRequest;
import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.core.AuthorizationGrantType;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationExchange;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationRequest;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationResponse;
import org.springframework.security.oauth2.jwt.JwtException;

class TelegramOidcTransportTest {
    private HttpServer server;
    private RSAKey signingKey;
    private String origin;
    private boolean gzip = true;
    private byte[] overrideKeys;

    @BeforeEach
    void start() throws Exception {
        signingKey = new RSAKeyGenerator(2048).keyID("telegram-test-key").generate();
        server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        origin = "http://127.0.0.1:" + server.getAddress().getPort();
        server.createContext("/jwks", exchange -> {
            byte[] bytes = overrideKeys != null ? overrideKeys
                    : new JWKSet(signingKey.toPublicJWK()).toString().getBytes(StandardCharsets.UTF_8);
            if (gzip && overrideKeys == null) bytes = compress(bytes);
            exchange.getResponseHeaders().set("Content-Type", "application/json");
            if (gzip) exchange.getResponseHeaders().set("Content-Encoding", "gzip");
            exchange.sendResponseHeaders(200, bytes.length);
            try (var output = exchange.getResponseBody()) { output.write(bytes); }
        });
        server.createContext("/token", exchange -> {
            exchange.getRequestBody().readAllBytes();
            byte[] bytes = compress("{\"access_token\":\"test-access\",\"token_type\":\"Bearer\",\"expires_in\":300,\"id_token\":\"test-id-token\"}".getBytes(StandardCharsets.UTF_8));
            exchange.getResponseHeaders().set("Content-Type", "application/json");
            exchange.getResponseHeaders().set("Content-Encoding", "gzip");
            exchange.sendResponseHeaders(200, bytes.length);
            try (var output = exchange.getResponseBody()) { output.write(bytes); }
        });
        server.start();
    }

    @AfterEach
    void stop() { if (server != null) server.stop(0); }

    @Test
    void acceptsSignedIdTokenWhenKeysAreGzipped() throws Exception {
        var decoder = new TelegramOidcConfiguration().telegramJwtDecoderFactory().createDecoder(registration());
        assertEquals("12345", decoder.decode(token(signingKey, origin, "test-client", Instant.now().plusSeconds(300))).getSubject());
    }

    @Test
    void acceptsUncompressedKeysToo() throws Exception {
        gzip = false;
        var decoder = new TelegramIdTokenDecoderFactory().createDecoder(registration());
        assertEquals("12345", decoder.decode(token(signingKey, origin, "test-client", Instant.now().plusSeconds(300))).getSubject());
    }

    @Test
    void gzipSupportPreservesIssuerAudienceExpiryAndSignatureChecks() throws Exception {
        var decoder = new TelegramIdTokenDecoderFactory().createDecoder(registration());
        String wrongIssuer = token(signingKey, "https://wrong.example", "test-client", Instant.now().plusSeconds(300));
        String wrongAudience = token(signingKey, origin, "wrong-client", Instant.now().plusSeconds(300));
        String expired = token(signingKey, origin, "test-client", Instant.now().minusSeconds(300));
        RSAKey other = new RSAKeyGenerator(2048).keyID(signingKey.getKeyID()).generate();
        String wrongSignature = token(other, origin, "test-client", Instant.now().plusSeconds(300));
        assertThrows(JwtException.class, () -> decoder.decode(wrongIssuer));
        assertThrows(JwtException.class, () -> decoder.decode(wrongAudience));
        assertThrows(JwtException.class, () -> decoder.decode(expired));
        assertThrows(JwtException.class, () -> decoder.decode(wrongSignature));
    }

    @Test
    void rejectsMalformedCompressedKeyResponse() throws Exception {
        overrideKeys = "not-gzip".getBytes(StandardCharsets.UTF_8);
        var decoder = new TelegramIdTokenDecoderFactory().createDecoder(registration());
        String token = token(signingKey, origin, "test-client", Instant.now().plusSeconds(300));
        assertThrows(JwtException.class, () -> decoder.decode(token));
    }

    @Test
    void rejectsOverlargeDecompressedResponse() throws Exception {
        overrideKeys = compress(new byte[1024 * 1024 + 1]);
        var decoder = new TelegramIdTokenDecoderFactory().createDecoder(registration());
        String token = token(signingKey, origin, "test-client", Instant.now().plusSeconds(300));
        assertThrows(JwtException.class, () -> decoder.decode(token));
    }

    @Test
    void authorizationCodeClientReadsGzippedTokenResponse() {
        var registration = registration();
        var request = OAuth2AuthorizationRequest.authorizationCode().authorizationUri(origin + "/authorize")
                .clientId("test-client").redirectUri(origin + "/callback").state("test-state").build();
        var response = OAuth2AuthorizationResponse.success("test-code").redirectUri(origin + "/callback")
                .state("test-state").build();
        var grant = new OAuth2AuthorizationCodeGrantRequest(registration, new OAuth2AuthorizationExchange(request, response));
        var tokens = new TelegramOidcConfiguration().telegramTokenResponseClient().getTokenResponse(grant);
        assertEquals("test-access", tokens.getAccessToken().getTokenValue());
        assertEquals("test-id-token", tokens.getAdditionalParameters().get("id_token"));
    }

    private ClientRegistration registration() {
        return ClientRegistration.withRegistrationId("telegram").clientId("test-client").clientSecret("test-secret")
                .authorizationGrantType(AuthorizationGrantType.AUTHORIZATION_CODE).redirectUri(origin + "/callback")
                .scope("openid", "profile").authorizationUri(origin + "/authorize").tokenUri(origin + "/token")
                .jwkSetUri(origin + "/jwks").issuerUri(origin).userNameAttributeName("sub").build();
    }

    private String token(RSAKey key, String issuer, String audience, Instant expiry) throws JOSEException {
        var claims = new JWTClaimsSet.Builder().issuer(issuer).subject("12345").audience(audience)
                .issueTime(Date.from(Instant.now().minusSeconds(600))).expirationTime(Date.from(expiry)).build();
        var token = new SignedJWT(new JWSHeader.Builder(JWSAlgorithm.RS256).keyID(key.getKeyID()).build(), claims);
        token.sign(new RSASSASigner(key));
        return token.serialize();
    }

    private static byte[] compress(byte[] input) throws java.io.IOException {
        var output = new ByteArrayOutputStream();
        try (var gzip = new GZIPOutputStream(output)) { gzip.write(input); }
        return output.toByteArray();
    }
}
