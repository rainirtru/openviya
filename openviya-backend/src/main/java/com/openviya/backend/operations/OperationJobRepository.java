package com.openviya.backend.operations;

import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.jdbc.core.namedparam.MapSqlParameterSource;
import org.springframework.jdbc.core.namedparam.NamedParameterJdbcTemplate;
import org.springframework.stereotype.Repository;

@Repository
public class OperationJobRepository {

    private static final RowMapper<ClaimedOperationJob> CLAIMED_JOB_MAPPER = (rs, rowNum) ->
            new ClaimedOperationJob(
                    rs.getObject("id", UUID.class),
                    rs.getString("kind"),
                    rs.getString("business_key"),
                    rs.getInt("payload_version"),
                    rs.getString("payload"),
                    rs.getInt("attempt_count"),
                    rs.getString("lease_owner"),
                    rs.getTimestamp("lease_until").toInstant(),
                    rs.getLong("lease_generation"));

    private final NamedParameterJdbcTemplate jdbc;

    public OperationJobRepository(NamedParameterJdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public UUID enqueue(String kind, String businessKey, int payloadVersion, String jsonPayload) {
        UUID id = UUID.randomUUID();
        MapSqlParameterSource parameters = new MapSqlParameterSource()
                .addValue("id", id)
                .addValue("kind", kind)
                .addValue("businessKey", businessKey)
                .addValue("payloadVersion", payloadVersion)
                .addValue("payload", jsonPayload);

        List<UUID> inserted = jdbc.query("""
                INSERT INTO operation_job (id, kind, business_key, payload_version, payload)
                VALUES (:id, :kind, :businessKey, :payloadVersion, CAST(:payload AS jsonb))
                ON CONFLICT (kind, business_key) DO NOTHING
                RETURNING id
                """, parameters, (rs, rowNum) -> rs.getObject("id", UUID.class));
        if (!inserted.isEmpty()) {
            return inserted.getFirst();
        }

        List<ExistingJob> existing = jdbc.query("""
                SELECT id, payload_version, payload = CAST(:payload AS jsonb) AS same_payload
                FROM operation_job
                WHERE kind = :kind AND business_key = :businessKey
                """, parameters, (rs, rowNum) -> new ExistingJob(
                rs.getObject("id", UUID.class),
                rs.getInt("payload_version"),
                rs.getBoolean("same_payload")));

        ExistingJob job = existing.stream().findFirst()
                .orElseThrow(() -> new IllegalStateException("Conflicting operation job disappeared."));
        if (job.payloadVersion() != payloadVersion || !job.samePayload()) {
            throw new IllegalStateException("Operation job key already exists with a different payload.");
        }
        return job.id();
    }

    public List<ClaimedOperationJob> claimReady(String workerId, int limit, Duration leaseDuration) {
        if (limit < 1) {
            throw new IllegalArgumentException("Claim limit and lease duration must be positive.");
        }
        requirePositiveLease(leaseDuration);
        MapSqlParameterSource parameters = new MapSqlParameterSource()
                .addValue("workerId", workerId)
                .addValue("limit", limit)
                .addValue("leaseMillis", leaseDuration.toMillis());

        return jdbc.query("""
                WITH candidates AS (
                    SELECT id
                    FROM operation_job
                    WHERE (status IN ('READY', 'RETRY_WAIT') AND next_attempt_at <= clock_timestamp())
                       OR (status = 'RUNNING' AND lease_until <= clock_timestamp())
                    ORDER BY CASE WHEN status = 'RUNNING' THEN lease_until ELSE next_attempt_at END,
                             created_at,
                             id
                    LIMIT :limit
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE operation_job job
                SET status = 'RUNNING',
                    attempt_count = job.attempt_count + 1,
                    lease_owner = :workerId,
                    lease_until = clock_timestamp() + (:leaseMillis * interval '1 millisecond'),
                    lease_generation = job.lease_generation + 1,
                    updated_at = clock_timestamp()
                FROM candidates
                WHERE job.id = candidates.id
                RETURNING job.id, job.kind, job.business_key, job.payload_version,
                          job.payload::text AS payload, job.attempt_count, job.lease_owner,
                          job.lease_until, job.lease_generation
                """, parameters, CLAIMED_JOB_MAPPER);
    }

    public boolean renewLease(ClaimedOperationJob job, Duration leaseDuration) {
        requirePositiveLease(leaseDuration);
        MapSqlParameterSource parameters = leaseParameters(job)
                .addValue("leaseMillis", leaseDuration.toMillis());
        return jdbc.update("""
                UPDATE operation_job
                SET lease_until = clock_timestamp() + (:leaseMillis * interval '1 millisecond'),
                    updated_at = clock_timestamp()
                WHERE id = :id AND status = 'RUNNING' AND lease_owner = :leaseOwner
                  AND lease_generation = :leaseGeneration AND lease_until > clock_timestamp()
                """, parameters) == 1;
    }

    public boolean complete(ClaimedOperationJob job) {
        return jdbc.update("""
                UPDATE operation_job
                SET status = 'DONE', lease_owner = NULL, lease_until = NULL,
                    updated_at = clock_timestamp()
                WHERE id = :id AND status = 'RUNNING' AND lease_owner = :leaseOwner
                  AND lease_generation = :leaseGeneration AND lease_until > clock_timestamp()
                """, leaseParameters(job)) == 1;
    }

    public boolean scheduleRetry(ClaimedOperationJob job, Instant nextAttemptAt, String errorCode) {
        MapSqlParameterSource parameters = leaseParameters(job)
                .addValue("nextAttemptAt", Timestamp.from(nextAttemptAt))
                .addValue("errorCode", errorCode);
        return jdbc.update("""
                UPDATE operation_job
                SET status = 'RETRY_WAIT', next_attempt_at = :nextAttemptAt,
                    last_error_code = :errorCode, lease_owner = NULL, lease_until = NULL,
                    updated_at = clock_timestamp()
                WHERE id = :id AND status = 'RUNNING' AND lease_owner = :leaseOwner
                  AND lease_generation = :leaseGeneration AND lease_until > clock_timestamp()
                """, parameters) == 1;
    }

    public boolean requireAttention(ClaimedOperationJob job, String errorCode) {
        MapSqlParameterSource parameters = leaseParameters(job).addValue("errorCode", errorCode);
        return jdbc.update("""
                UPDATE operation_job
                SET status = 'NEEDS_ATTENTION', last_error_code = :errorCode,
                    lease_owner = NULL, lease_until = NULL, updated_at = clock_timestamp()
                WHERE id = :id AND status = 'RUNNING' AND lease_owner = :leaseOwner
                  AND lease_generation = :leaseGeneration AND lease_until > clock_timestamp()
                """, parameters) == 1;
    }

    private MapSqlParameterSource leaseParameters(ClaimedOperationJob job) {
        return new MapSqlParameterSource()
                .addValue("id", job.id())
                .addValue("leaseOwner", job.leaseOwner())
                .addValue("leaseGeneration", job.leaseGeneration());
    }

    private void requirePositiveLease(Duration leaseDuration) {
        if (leaseDuration.isNegative() || leaseDuration.isZero() || leaseDuration.toMillis() < 1) {
            throw new IllegalArgumentException("Lease duration must be at least one millisecond.");
        }
    }

    private record ExistingJob(UUID id, int payloadVersion, boolean samePayload) {
    }
}
