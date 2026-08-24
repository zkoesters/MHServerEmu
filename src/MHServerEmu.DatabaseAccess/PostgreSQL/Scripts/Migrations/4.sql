-- Add guild data and player logout timestamps.
ALTER TABLE player ADD COLUMN last_logout_time bigint;
UPDATE player SET last_logout_time=0;

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
