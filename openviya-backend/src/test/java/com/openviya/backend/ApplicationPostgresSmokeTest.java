package com.openviya.backend;

import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.eatthepath.otp.TimeBasedOneTimePasswordGenerator;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.context.ApplicationContext;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.boot.test.web.server.LocalServerPort;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.testcontainers.containers.PostgreSQLContainer;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;
import com.openviya.backend.audit.AuditActor;
import com.openviya.backend.audit.AuditEventRepository;
import com.openviya.backend.identity.ExternalIdentity;
import com.openviya.backend.identity.TelegramProfile;
import com.openviya.backend.identity.UserIdentityRepository;
import com.openviya.backend.subscriptions.ActivatedGrant;
import com.openviya.backend.subscriptions.ActivatedSubscription;
import com.openviya.backend.subscriptions.SubscriptionActivationService;
import java.time.Instant;
import java.time.Duration;
import java.net.CookieManager;
import java.net.CookiePolicy;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import javax.crypto.spec.SecretKeySpec;
import tools.jackson.databind.JsonNode;
import tools.jackson.databind.json.JsonMapper;

@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
@Testcontainers(disabledWithoutDocker = true)
class ApplicationPostgresSmokeTest {

    private static final String BOOTSTRAP_PASSWORD = "integration-test-owner-password";
    private static final String TOTP_KEY = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    @Container
    private static final PostgreSQLContainer<?> postgres = new PostgreSQLContainer<>("postgres:18.6-alpine3.23");

    @DynamicPropertySource
    static void configureDatabase(DynamicPropertyRegistry properties) {
        properties.add("spring.datasource.url", postgres::getJdbcUrl);
        properties.add("spring.datasource.username", postgres::getUsername);
        properties.add("spring.datasource.password", postgres::getPassword);
        properties.add("openviya.auth.bootstrap-owner.enabled", () -> true);
        properties.add("openviya.auth.bootstrap-owner.login", () -> "integration-owner");
        properties.add("openviya.auth.bootstrap-owner.password", () -> BOOTSTRAP_PASSWORD);
        properties.add("openviya.auth.totp-encryption-key", () -> TOTP_KEY);
        properties.add("spring.servlet.session.cookie.secure", () -> false);
    }

    @Autowired
    private ApplicationContext applicationContext;

    @Autowired
    private JdbcTemplate jdbc;

    @Autowired
    private UserIdentityRepository identities;

    @Autowired
    private AuditEventRepository auditEvents;

    @Autowired
    private SubscriptionActivationService activations;

    @LocalServerPort
    private int port;

    @Test
    void applicationStartsWithFreshPostgresMigrationsAndApiInfoController() {
        assertTrue(applicationContext.containsBean("apiInfoController"));
        assertEquals(6, jdbc.queryForObject(
                "SELECT count(*) FROM flyway_schema_history WHERE success", Integer.class));
    }

    @Test
    void concurrentTelegramResolutionCreatesOneInternalIdentity() throws Exception {
        TelegramProfile profile = new TelegramProfile("712345678", "openviya_test", "OpenViya Test");
        Instant verifiedAt = Instant.parse("2026-10-15T10:00:00Z");
        CountDownLatch ready = new CountDownLatch(2);
        CountDownLatch start = new CountDownLatch(1);
        try (var executor = Executors.newFixedThreadPool(2)) {
            var first = executor.submit(() -> resolveAfterBarrier(profile, verifiedAt, ready, start));
            var second = executor.submit(() -> resolveAfterBarrier(profile, verifiedAt, ready, start));
            assertTrue(ready.await(5, TimeUnit.SECONDS));
            start.countDown();

            ExternalIdentity one = first.get(10, TimeUnit.SECONDS);
            ExternalIdentity two = second.get(10, TimeUnit.SECONDS);
            assertEquals(one.userId(), two.userId());
            assertEquals(1, jdbc.queryForObject(
                    "SELECT count(*) FROM external_identity WHERE provider_subject = '712345678'", Integer.class));
            assertEquals(1, jdbc.queryForObject(
                    "SELECT count(*) FROM app_user u JOIN external_identity i ON i.user_id = u.id "
                            + "WHERE i.provider_subject = '712345678'", Integer.class));
        }
    }

    @Test
    void auditStoresSafeChangesAndRejectsNestedSecretKeys() {
        UUID actorId = UUID.randomUUID();
        UUID eventId = auditEvents.append(new AuditActor(AuditActor.Kind.STAFF, actorId),
                "staff.roles.changed", "STAFF", actorId.toString(), "Correct initial role assignment",
                Map.of("roles", java.util.List.of("SUPPORT")), Map.of("roles", java.util.List.of("ADMIN")),
                "test-correlation");

        assertEquals(1, jdbc.queryForObject("SELECT count(*) FROM audit_event WHERE id = ?", Integer.class, eventId));
        assertThrows(IllegalArgumentException.class,
                () -> auditEvents.append(new AuditActor(AuditActor.Kind.SYSTEM, null),
                        "staff.test", "STAFF", actorId.toString(), null, null,
                        Map.of("nested", Map.of("accessToken", "dummy")), "test-correlation"));
        assertEquals(1, jdbc.queryForObject(
                "SELECT count(*) FROM audit_event WHERE action = 'staff.roles.changed'", Integer.class));
    }

    @Test
    void ownerRequiresPasswordAndTotpBeforeAdministrativeEndpointWorks() throws Exception {
        CookieManager cookies = new CookieManager(null, CookiePolicy.ACCEPT_ALL);
        HttpClient client = HttpClient.newBuilder().cookieHandler(cookies).build();
        JsonMapper json = JsonMapper.builder().build();
        JsonNode csrfResponse = json.readTree(send(client, "GET", "/api/v1/staff/auth/csrf", null, null, null).body());
        String headerName = csrfResponse.get("headerName").stringValue();
        String csrfToken = csrfResponse.get("token").stringValue();

        HttpResponse<String> passwordStep = send(client, "POST", "/api/v1/staff/auth/login",
                "{\"login\":\"integration-owner\",\"password\":\"" + BOOTSTRAP_PASSWORD + "\"}",
                headerName, csrfToken);
        assertEquals(200, passwordStep.statusCode());
        HttpResponse<String> beforeTotp = send(client, "POST", "/api/v1/admin/staff",
                "{\"login\":\"premature-admin\",\"password\":\"another-strong-test-password\","
                        + "\"roles\":[\"ADMIN\"],\"reason\":\"Must remain unauthenticated\"}",
                headerName, csrfToken);
        assertEquals(403, beforeTotp.statusCode());
        assertEquals(0, jdbc.queryForObject(
                "SELECT count(*) FROM staff_account WHERE login_normalized = 'premature-admin'", Integer.class));
        String challengeId = json.readTree(passwordStep.body()).get("challengeId").stringValue();
        HttpResponse<String> enrollment = send(client, "POST", "/api/v1/staff/auth/totp/enrollment",
                "{\"challengeId\":\"" + challengeId + "\"}", headerName, csrfToken);
        assertEquals(200, enrollment.statusCode());
        String manualEntryKey = json.readTree(enrollment.body()).get("manualEntryKey").stringValue();
        String code = totpCode(manualEntryKey);

        HttpResponse<String> completed = send(client, "POST", "/api/v1/staff/auth/totp/verify",
                "{\"challengeId\":\"" + challengeId + "\",\"code\":\"" + code + "\"}",
                headerName, csrfToken);
        assertEquals(200, completed.statusCode());
        assertEquals(8, json.readTree(completed.body()).get("recoveryCodes").size());

        HttpResponse<String> adminCreate = send(client, "POST", "/api/v1/admin/staff",
                "{\"login\":\"new-admin\",\"password\":\"another-strong-test-password\","
                        + "\"roles\":[\"ADMIN\"],\"reason\":\"Integration test setup\"}",
                headerName, csrfToken);
        assertEquals(200, adminCreate.statusCode(), adminCreate.body());
        assertEquals(1, jdbc.queryForObject(
                "SELECT count(*) FROM staff_account WHERE login_normalized = 'new-admin'", Integer.class));
    }

    @Test
    void commercialGrantConfirmationAndActivationPersistOnceInPostgres() {
        UUID userId = UUID.randomUUID();
        UUID subscriptionId = UUID.randomUUID();
        UUID grantId = UUID.randomUUID();
        UUID orderId = UUID.randomUUID();
        Instant confirmedAt = Instant.parse("2026-10-15T10:00:00Z");
        jdbc.update("INSERT INTO app_user (id) VALUES (?)", userId);
        jdbc.update("INSERT INTO user_subscription (id, user_id) VALUES (?, ?)", subscriptionId, userId);
        jdbc.update("""
                INSERT INTO subscription_grant
                    (id, subscription_id, source_type, source_id, duration_seconds)
                VALUES (?, ?, 'PURCHASE', ?, ?)
                """, grantId, subscriptionId, orderId, java.time.Duration.ofDays(30).toSeconds());

        activations.confirmCommercialGrant(grantId, confirmedAt, true);
        ActivatedGrant first = activations.activateCommercialGrant(grantId, confirmedAt);
        ActivatedGrant retry = activations.activateCommercialGrant(grantId, confirmedAt.plusSeconds(60));

        assertEquals(confirmedAt, first.startsAt());
        assertEquals(confirmedAt.plus(java.time.Duration.ofDays(30)), first.endsAt());
        assertEquals(first.endsAt(), retry.endsAt());
        assertEquals(1, jdbc.queryForObject(
                "SELECT count(*) FROM subscription_grant WHERE id = ? AND state = 'ACTIVE'", Integer.class, grantId));
    }

    @Test
    void trialActivationPersistsSameEndOnRetryInPostgres() {
        UUID userId = UUID.randomUUID();
        UUID subscriptionId = UUID.randomUUID();
        UUID claimId = UUID.randomUUID();
        UUID grantId = UUID.randomUUID();
        Instant confirmedAt = Instant.parse("2026-10-15T10:00:00Z");
        jdbc.update("INSERT INTO app_user (id) VALUES (?)", userId);
        jdbc.update("INSERT INTO user_subscription (id, user_id, state, device_limit) VALUES (?, ?, 'PENDING', 2)",
                subscriptionId, userId);
        jdbc.update("""
                INSERT INTO trial_claim (id, telegram_user_id, user_id, subscription_id)
                VALUES (?, ?, ?, ?)
                """, claimId, "712345679", userId, subscriptionId);
        jdbc.update("""
                INSERT INTO subscription_grant
                    (id, subscription_id, source_type, source_id, duration_seconds)
                VALUES (?, ?, 'TRIAL', ?, ?)
                """, grantId, subscriptionId, claimId, java.time.Duration.ofHours(24).toSeconds());

        ActivatedSubscription first = activations.activateTrial(claimId, confirmedAt);
        ActivatedSubscription retry = activations.activateTrial(claimId, confirmedAt.plusSeconds(60));

        assertEquals(confirmedAt, first.activatedAt());
        assertEquals(confirmedAt.plus(java.time.Duration.ofHours(24)), first.endsAt());
        assertEquals(first.endsAt(), retry.endsAt());
    }

    private ExternalIdentity resolveAfterBarrier(
            TelegramProfile profile, Instant verifiedAt, CountDownLatch ready, CountDownLatch start) {
        ready.countDown();
        try {
            if (!start.await(5, TimeUnit.SECONDS)) {
                throw new IllegalStateException("Identity start barrier timed out");
            }
            return identities.resolveTelegramIdentity(profile, verifiedAt);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Identity resolution interrupted", exception);
        }
    }

    private HttpResponse<String> send(
            HttpClient client, String method, String path, String body, String csrfHeader, String csrfToken)
            throws Exception {
        HttpRequest.Builder request = HttpRequest.newBuilder(URI.create("http://localhost:" + port + path))
                .header("Accept", "application/json");
        if (body == null) {
            request.GET();
        } else {
            request.header("Content-Type", "application/json")
                    .header(csrfHeader, csrfToken)
                    .method(method, HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8));
        }
        return client.send(request.build(), HttpResponse.BodyHandlers.ofString());
    }

    private static String totpCode(String base32Secret) throws Exception {
        byte[] secret = decodeBase32(base32Secret);
        var generator = new TimeBasedOneTimePasswordGenerator(Duration.ofSeconds(30), 6, "HmacSHA1");
        var key = new SecretKeySpec(secret, "HmacSHA1");
        return generator.generateOneTimePasswordString(key, Instant.now());
    }

    private static byte[] decodeBase32(String encoded) {
        String alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        java.io.ByteArrayOutputStream result = new java.io.ByteArrayOutputStream();
        int buffer = 0;
        int bits = 0;
        for (char value : encoded.toCharArray()) {
            buffer = (buffer << 5) | alphabet.indexOf(value);
            bits += 5;
            if (bits >= 8) {
                result.write((buffer >>> (bits - 8)) & 0xff);
                bits -= 8;
            }
        }
        return result.toByteArray();
    }
}
