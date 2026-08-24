-- Replace the legacy placeholder tables while preserving existing accounts.
CREATE UNIQUE INDEX ux_account_email_ci ON account (lower(email));
CREATE UNIQUE INDEX ux_account_player_name_ci ON account (lower(player_name));

DROP TABLE avatar;
DROP TABLE player;

CREATE TABLE player (
    db_guid bigint PRIMARY KEY,
    archive_data bytea,
    start_target bigint,
    start_target_region_override bigint,
    aoi_volume integer
);

CREATE TABLE avatar (
    db_guid bigint PRIMARY KEY,
    container_db_guid bigint,
    inventory_proto_guid bigint,
    slot bigint CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid bigint,
    archive_data bytea
);

CREATE INDEX ix_avatar_container_db_guid ON avatar (container_db_guid);

CREATE TABLE team_up (
    db_guid bigint PRIMARY KEY,
    container_db_guid bigint,
    inventory_proto_guid bigint,
    slot bigint CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid bigint,
    archive_data bytea
);

CREATE INDEX ix_team_up_container_db_guid ON team_up (container_db_guid);

CREATE TABLE item (
    db_guid bigint PRIMARY KEY,
    container_db_guid bigint,
    inventory_proto_guid bigint,
    slot bigint CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid bigint,
    archive_data bytea
);

CREATE INDEX ix_item_container_db_guid ON item (container_db_guid);

CREATE TABLE controlled_entity (
    db_guid bigint PRIMARY KEY,
    container_db_guid bigint,
    inventory_proto_guid bigint,
    slot bigint CHECK (slot BETWEEN 0 AND 4294967295),
    entity_proto_guid bigint,
    archive_data bytea
);

CREATE INDEX ix_controlled_entity_container_db_guid ON controlled_entity (container_db_guid);
