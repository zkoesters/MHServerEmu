-- Replace the legacy placeholder tables while preserving existing accounts.
CREATE UNIQUE INDEX ux_account_email_ci ON account (email);
CREATE UNIQUE INDEX ux_account_player_name_ci ON account (player_name);

DROP TABLE avatar;
DROP TABLE player;

CREATE TABLE player (
    db_guid BIGINT PRIMARY KEY,
    archive_data LONGBLOB,
    start_target BIGINT,
    start_target_region_override BIGINT,
    aoi_volume INT
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
