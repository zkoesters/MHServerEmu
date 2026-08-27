CREATE TABLE mhserveremu_schema (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    version INT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE account (
    id BIGINT PRIMARY KEY,
    email VARCHAR(320) COLLATE utf8mb4_unicode_ci NOT NULL,
    player_name VARCHAR(16) COLLATE utf8mb4_unicode_ci NOT NULL,
    password_hash BLOB NOT NULL,
    salt BLOB NOT NULL,
    user_level INT NOT NULL,
    flags INT NOT NULL,
    UNIQUE KEY ux_account_email_ci (email),
    UNIQUE KEY ux_account_player_name_ci (player_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE player (
    db_guid BIGINT PRIMARY KEY,
    archive_data LONGBLOB,
    start_target BIGINT,
    aoi_volume INT,
    gazillionite_balance BIGINT,
    last_logout_time BIGINT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE avatar (
    db_guid BIGINT PRIMARY KEY,
    container_db_guid BIGINT,
    inventory_proto_guid BIGINT,
    slot BIGINT CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid BIGINT,
    archive_data LONGBLOB,
    KEY ix_avatar_container_db_guid (container_db_guid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE team_up (
    db_guid BIGINT PRIMARY KEY,
    container_db_guid BIGINT,
    inventory_proto_guid BIGINT,
    slot BIGINT CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid BIGINT,
    archive_data LONGBLOB,
    KEY ix_team_up_container_db_guid (container_db_guid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE item (
    db_guid BIGINT PRIMARY KEY,
    container_db_guid BIGINT,
    inventory_proto_guid BIGINT,
    slot BIGINT CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid BIGINT,
    archive_data LONGBLOB,
    KEY ix_item_container_db_guid (container_db_guid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE controlled_entity (
    db_guid BIGINT PRIMARY KEY,
    container_db_guid BIGINT,
    inventory_proto_guid BIGINT,
    slot BIGINT CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid BIGINT,
    archive_data LONGBLOB,
    KEY ix_controlled_entity_container_db_guid (container_db_guid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

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
