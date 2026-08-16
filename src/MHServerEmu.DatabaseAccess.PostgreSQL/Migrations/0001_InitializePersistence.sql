CREATE SCHEMA mhserveremu;

CREATE TABLE mhserveremu.schema_migrations (
    version integer PRIMARY KEY,
    name text NOT NULL,
    checksum text NOT NULL,
    application_version text NOT NULL,
    applied_at_utc timestamp with time zone NOT NULL,
    duration_ms integer NOT NULL CHECK (duration_ms >= 0)
);

CREATE TABLE mhserveremu.application_metadata (
    identity_normalization_version integer PRIMARY KEY CHECK (identity_normalization_version = 1)
);

INSERT INTO mhserveremu.application_metadata (identity_normalization_version) VALUES (1);

CREATE TABLE mhserveremu.writer_fence (
    singleton boolean PRIMARY KEY CHECK (singleton),
    generation bigint NOT NULL,
    owner_id uuid NOT NULL
);

INSERT INTO mhserveremu.writer_fence (singleton, generation, owner_id) VALUES (true, 0, '00000000-0000-0000-0000-000000000000');
