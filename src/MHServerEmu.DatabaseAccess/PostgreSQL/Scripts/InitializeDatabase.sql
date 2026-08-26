CREATE TABLE mhserveremu_schema (
    id smallint PRIMARY KEY CHECK (id = 1),
    version integer NOT NULL
);

CREATE TABLE account (
    id bigint PRIMARY KEY,
    email text NOT NULL,
    player_name text NOT NULL,
    password_hash bytea NOT NULL,
    salt bytea NOT NULL,
    user_level integer NOT NULL,
    flags integer NOT NULL
);

CREATE UNIQUE INDEX ux_account_email_ci ON account (lower(email));
CREATE UNIQUE INDEX ux_account_player_name_ci ON account (lower(player_name));

CREATE TABLE player (
    db_guid bigint PRIMARY KEY,
    archive_data bytea,
    start_target bigint,
    aoi_volume integer,
    gazillionite_balance bigint,
    last_logout_time bigint,
    flags bigint NOT NULL DEFAULT 0
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

CREATE TABLE guild (
    id bigint PRIMARY KEY,
    name text NOT NULL,
    motd text NOT NULL,
    creator_db_guid bigint,
    creation_time bigint
);

CREATE UNIQUE INDEX ux_guild_name_ci ON guild (lower(name));

CREATE TABLE guild_member (
    player_db_guid bigint PRIMARY KEY,
    guild_id bigint NOT NULL,
    membership bigint NOT NULL
);

INSERT INTO mhserveremu_schema (id, version)
VALUES (1, 7);
