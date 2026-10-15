CREATE TABLE user_subscription
(
    id            uuid PRIMARY KEY,
    user_id       uuid NOT NULL UNIQUE REFERENCES app_user (id),
    state         varchar(20) NOT NULL DEFAULT 'INACTIVE'
        CHECK (state IN ('INACTIVE', 'PENDING', 'ACTIVE', 'EXPIRED', 'SUSPENDED')),
    ends_at       timestamptz,
    device_limit  smallint NOT NULL DEFAULT 0 CHECK (device_limit BETWEEN 0 AND 5),
    version       bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_subscription_active_end CHECK (state <> 'ACTIVE' OR ends_at IS NOT NULL)
);

CREATE TABLE trial_claim
(
    id                uuid PRIMARY KEY,
    telegram_user_id  varchar(16) NOT NULL UNIQUE,
    user_id           uuid NOT NULL UNIQUE REFERENCES app_user (id),
    subscription_id   uuid NOT NULL UNIQUE REFERENCES user_subscription (id),
    state             varchar(20) NOT NULL DEFAULT 'REQUESTED'
        CHECK (state IN ('REQUESTED', 'ISSUING', 'ACTIVE', 'FAILED', 'SUPERSEDED')),
    activated_at      timestamptz,
    last_error_code   varchar(100),
    created_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_trial_claim_activation CHECK ((state = 'ACTIVE') = (activated_at IS NOT NULL))
);

CREATE TABLE subscription_grant
(
    id                uuid PRIMARY KEY,
    subscription_id   uuid NOT NULL REFERENCES user_subscription (id),
    source_type       varchar(20) NOT NULL CHECK (source_type IN ('TRIAL', 'PURCHASE', 'PROMO')),
    source_id         uuid NOT NULL,
    duration_seconds  bigint NOT NULL CHECK (duration_seconds > 0),
    state             varchar(20) NOT NULL DEFAULT 'PENDING'
        CHECK (state IN ('PENDING', 'ACTIVE', 'REVOKED', 'SUPERSEDED')),
    starts_at         timestamptz,
    ends_at           timestamptz,
    created_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_subscription_grant_source UNIQUE (source_type, source_id),
    CONSTRAINT ck_subscription_grant_interval CHECK (
        (starts_at IS NULL AND ends_at IS NULL)
        OR (starts_at IS NOT NULL AND ends_at IS NOT NULL AND ends_at > starts_at)
    ),
    CONSTRAINT ck_subscription_grant_active_interval CHECK (state <> 'ACTIVE' OR starts_at IS NOT NULL)
);

CREATE INDEX ix_subscription_grant_history
    ON subscription_grant (subscription_id, created_at, id);
