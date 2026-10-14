package com.openviya.backend.audit;

import java.util.Map;
import java.util.UUID;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Repository;
import org.springframework.transaction.annotation.Transactional;
import tools.jackson.databind.json.JsonMapper;

@Repository
public class AuditEventRepository {

    private static final String[] FORBIDDEN_KEYS = {
        "password", "secret", "token", "authorization", "credential", "totp", "recoverycode",
        "subscriptionurl", "subscriptionlink", "cardnumber", "carddata", "cvv", "pan"
    };

    private final JdbcTemplate jdbcTemplate;
    private final JsonMapper jsonMapper;

    public AuditEventRepository(JdbcTemplate jdbcTemplate, JsonMapper jsonMapper) {
        this.jdbcTemplate = jdbcTemplate;
        this.jsonMapper = jsonMapper;
    }

    @Transactional
    public UUID append(
            AuditActor actor,
            String action,
            String subjectKind,
            String subjectId,
            String reason,
            Map<String, ?> beforeState,
            Map<String, ?> afterState,
            String correlationId) {
        if (actor == null) {
            throw new IllegalArgumentException("Audit actor is required");
        }
        if (actor.kind() == AuditActor.Kind.STAFF && (reason == null || reason.isBlank())) {
            throw new IllegalArgumentException("Administrative audit events require a reason");
        }
        validateText(action, "action", 120);
        validateText(subjectKind, "subject kind", 80);
        validateText(subjectId, "subject id", 255);
        validateText(reason, "reason", 1000);
        validateText(correlationId, "correlation id", 80);
        validateSafeKeys(beforeState);
        validateSafeKeys(afterState);

        UUID id = UUID.randomUUID();
        jdbcTemplate.update(
                """
                INSERT INTO audit_event
                    (id, actor_kind, actor_id, action, subject_kind, subject_id, reason,
                     before_state, after_state, correlation_id)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?::jsonb, ?::jsonb, ?)
                """,
                id,
                actor.kind().name(),
                actor.id(),
                action,
                subjectKind,
                subjectId,
                reason,
                toJson(beforeState),
                toJson(afterState),
                correlationId);
        return id;
    }

    private String toJson(Map<String, ?> state) {
        if (state == null) {
            return null;
        }
        try {
            return jsonMapper.writeValueAsString(state);
        } catch (RuntimeException exception) {
            throw new IllegalArgumentException("Audit state cannot be serialized", exception);
        }
    }

    private static void validateSafeKeys(Object value) {
        if (value instanceof Map<?, ?> map) {
            map.forEach((key, nestedValue) -> {
                if (!(key instanceof String name)) {
                    throw new IllegalArgumentException("Audit state keys must be strings");
                }
                String normalized = name.replaceAll("[^A-Za-z0-9]", "").toLowerCase();
                for (String forbidden : FORBIDDEN_KEYS) {
                    if (normalized.contains(forbidden)) {
                        throw new IllegalArgumentException("Sensitive field cannot be written to audit state");
                    }
                }
                validateSafeKeys(nestedValue);
            });
        } else if (value instanceof Iterable<?> values) {
            values.forEach(AuditEventRepository::validateSafeKeys);
        }
    }

    private static void validateText(String value, String field, int maxLength) {
        if (value != null && value.length() > maxLength) {
            throw new IllegalArgumentException("Audit " + field + " exceeds " + maxLength + " characters");
        }
    }
}
