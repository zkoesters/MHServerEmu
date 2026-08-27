-- Add guild data and player logout timestamps.
ALTER TABLE player ADD COLUMN last_logout_time BIGINT;
UPDATE player SET last_logout_time=0;

CREATE TABLE guild (
    id BIGINT PRIMARY KEY,
    name VARCHAR(320) COLLATE utf8mb4_unicode_ci NOT NULL,
    motd VARCHAR(320) COLLATE utf8mb4_unicode_ci NOT NULL,
    creator_db_guid BIGINT,
    creation_time BIGINT,
    UNIQUE KEY ux_guild_name_ci (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE guild_member (
    player_db_guid BIGINT PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    membership BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
