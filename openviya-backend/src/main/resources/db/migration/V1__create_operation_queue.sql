CREATE TABLE operation_job
(
    id               uuid PRIMARY KEY,
    kind             varchar(80)  NOT NULL,
    business_key     varchar(200) NOT NULL,
    payload_version  integer      NOT NULL CHECK (payload_version > 0),
    payload          jsonb        NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    status           varchar(24)  NOT NULL DEFAULT 'READY'
        CHECK (status IN ('READY', 'RUNNING', 'RETRY_WAIT', 'DONE', 'NEEDS_ATTENTION')),
    attempt_count    integer      NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at  timestamptz  NOT NULL DEFAULT clock_timestamp(),
    lease_owner      varchar(200),
    lease_until      timestamptz,
    lease_generation bigint       NOT NULL DEFAULT 0 CHECK (lease_generation >= 0),
    last_error_code  varchar(100),
    created_at       timestamptz  NOT NULL DEFAULT clock_timestamp(),
    updated_at       timestamptz  NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_operation_job_business_key UNIQUE (kind, business_key),
    CONSTRAINT ck_operation_job_lease_state CHECK (
        (status = 'RUNNING' AND lease_owner IS NOT NULL AND lease_until IS NOT NULL)
        OR (status <> 'RUNNING' AND lease_owner IS NULL AND lease_until IS NULL)
    )
);

CREATE INDEX ix_operation_job_ready
    ON operation_job (next_attempt_at, created_at, id)
    WHERE status IN ('READY', 'RETRY_WAIT');

CREATE INDEX ix_operation_job_expired_lease
    ON operation_job (lease_until, created_at, id)
    WHERE status = 'RUNNING';

CREATE INDEX ix_operation_job_attention
    ON operation_job (updated_at, id)
    WHERE status = 'NEEDS_ATTENTION';
