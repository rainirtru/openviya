CREATE TABLE app_user
(
    id          uuid PRIMARY KEY,
    status      varchar(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'SUSPENDED', 'CLOSED')),
    created_at  timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE external_identity
(
    id                  uuid PRIMARY KEY,
    user_id             uuid NOT NULL REFERENCES app_user (id),
    provider            varchar(24) NOT NULL CHECK (provider IN ('TELEGRAM', 'EMAIL')),
    provider_subject    varchar(255) NOT NULL,
    username            varchar(255),
    display_name        varchar(255),
    verified_at         timestamptz,
    last_seen_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_external_identity_subject UNIQUE (provider, provider_subject),
    CONSTRAINT uq_external_identity_user_provider UNIQUE (user_id, provider),
    CONSTRAINT ck_external_identity_subject_nonempty CHECK (length(provider_subject) > 0)
);

CREATE INDEX ix_external_identity_user ON external_identity (user_id, provider);

CREATE TABLE staff_account
(
    id                  uuid PRIMARY KEY,
    login               varchar(120) NOT NULL,
    login_normalized    varchar(120) NOT NULL UNIQUE,
    password_hash       varchar(255) NOT NULL,
    status              varchar(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'ACTIVE', 'LOCKED', 'DISABLED')),
    auth_version        integer NOT NULL DEFAULT 0 CHECK (auth_version >= 0),
    password_changed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_login_at       timestamptz,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_staff_account_login_nonempty CHECK (length(login_normalized) > 0)
);

CREATE TABLE staff_role
(
    staff_id    uuid NOT NULL REFERENCES staff_account (id),
    role        varchar(24) NOT NULL CHECK (role IN ('OWNER', 'ADMIN', 'SUPPORT')),
    assigned_by uuid REFERENCES staff_account (id),
    assigned_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (staff_id, role)
);

CREATE TABLE staff_totp_credential
(
    staff_id               uuid PRIMARY KEY REFERENCES staff_account (id),
    secret_ciphertext      bytea NOT NULL,
    encryption_key_version varchar(80) NOT NULL,
    confirmed_at           timestamptz,
    last_accepted_step     bigint,
    created_at             timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_staff_totp_ciphertext_nonempty CHECK (octet_length(secret_ciphertext) > 0),
    CONSTRAINT ck_staff_totp_step_nonnegative CHECK (last_accepted_step IS NULL OR last_accepted_step >= 0)
);

CREATE TABLE staff_recovery_code
(
    id          uuid PRIMARY KEY,
    staff_id    uuid NOT NULL REFERENCES staff_account (id),
    code_hash   varchar(255) NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT clock_timestamp(),
    consumed_at timestamptz,
    CONSTRAINT uq_staff_recovery_code_hash UNIQUE (staff_id, code_hash)
);

CREATE INDEX ix_staff_recovery_code_available
    ON staff_recovery_code (staff_id, created_at)
    WHERE consumed_at IS NULL;

CREATE TABLE staff_auth_challenge
(
    id                    uuid PRIMARY KEY,
    staff_id              uuid NOT NULL REFERENCES staff_account (id),
    password_verified_at  timestamptz NOT NULL,
    expires_at            timestamptz NOT NULL,
    consumed_at           timestamptz,
    failed_attempts       integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    CONSTRAINT ck_staff_auth_challenge_expiry CHECK (expires_at > password_verified_at)
);

CREATE INDEX ix_staff_auth_challenge_expiry
    ON staff_auth_challenge (expires_at)
    WHERE consumed_at IS NULL;

CREATE TABLE auth_rate_limit
(
    bucket_key  varchar(255) PRIMARY KEY,
    window_start timestamptz NOT NULL,
    attempts    integer NOT NULL CHECK (attempts >= 0),
    blocked_until timestamptz,
    updated_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX ix_auth_rate_limit_cleanup ON auth_rate_limit (updated_at);

CREATE TABLE audit_event
(
    id             uuid PRIMARY KEY,
    actor_kind     varchar(16) NOT NULL CHECK (actor_kind IN ('USER', 'STAFF', 'SYSTEM')),
    actor_id       uuid,
    action         varchar(120) NOT NULL,
    subject_kind   varchar(80) NOT NULL,
    subject_id     varchar(255),
    reason         varchar(1000),
    before_state  jsonb,
    after_state   jsonb,
    correlation_id varchar(80),
    occurred_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_audit_event_actor CHECK (
        (actor_kind = 'SYSTEM' AND actor_id IS NULL)
        OR (actor_kind <> 'SYSTEM' AND actor_id IS NOT NULL)
    ),
    CONSTRAINT ck_audit_event_before_object CHECK (before_state IS NULL OR jsonb_typeof(before_state) = 'object'),
    CONSTRAINT ck_audit_event_after_object CHECK (after_state IS NULL OR jsonb_typeof(after_state) = 'object')
);

CREATE INDEX ix_audit_event_subject ON audit_event (subject_kind, subject_id, occurred_at DESC);
CREATE INDEX ix_audit_event_actor ON audit_event (actor_kind, actor_id, occurred_at DESC);
CREATE INDEX ix_audit_event_correlation ON audit_event (correlation_id) WHERE correlation_id IS NOT NULL;
