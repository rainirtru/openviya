ALTER TABLE staff_auth_challenge
    ADD COLUMN auth_version integer NOT NULL DEFAULT 0 CHECK (auth_version >= 0);
