ALTER TABLE subscription_grant
    ADD COLUMN confirmed_at timestamptz,
    ADD COLUMN paid_at timestamptz,
    ADD CONSTRAINT ck_subscription_grant_paid_confirmation
        CHECK (paid_at IS NULL OR confirmed_at IS NOT NULL);
