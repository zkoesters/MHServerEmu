CREATE TABLE mhserveremu.account (
    id bigint PRIMARY KEY,
    email text NOT NULL,
    normalized_email text NOT NULL,
    player_name text NOT NULL,
    normalized_player_name text NOT NULL,
    password_hash bytea NOT NULL,
    password_salt bytea NOT NULL,
    password_algorithm integer NOT NULL DEFAULT 1,
    password_format_version integer NOT NULL DEFAULT 1,
    password_iterations integer NOT NULL DEFAULT 210000,
    password_key_size integer NOT NULL DEFAULT 64,
    credential_version integer NOT NULL DEFAULT 1,
    game_security_version integer NOT NULL DEFAULT 1,
    user_level integer NOT NULL,
    flags integer NOT NULL,
    email_verified_at_utc timestamp with time zone,
    revision bigint NOT NULL DEFAULT 0,
    created_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT account_password_hash_length CHECK (octet_length(password_hash) = 64),
    CONSTRAINT account_password_salt_length CHECK (octet_length(password_salt) = 64),
    CONSTRAINT account_password_algorithm CHECK (password_algorithm = 1),
    CONSTRAINT account_password_format_version CHECK (password_format_version = 1),
    CONSTRAINT account_password_iterations CHECK (password_iterations = 210000),
    CONSTRAINT account_password_key_size CHECK (password_key_size = 64),
    CONSTRAINT account_credential_version CHECK (credential_version >= 1),
    CONSTRAINT account_game_security_version CHECK (game_security_version >= 1),
    CONSTRAINT account_user_level CHECK (user_level BETWEEN 0 AND 2),
    CONSTRAINT account_revision CHECK (revision >= 0),
    CONSTRAINT account_normalized_email_unique UNIQUE (normalized_email),
    CONSTRAINT account_normalized_player_name_unique UNIQUE (normalized_player_name)
);

CREATE TABLE mhserveremu.player_profile (
    account_id bigint PRIMARY KEY REFERENCES mhserveremu.account (id) ON DELETE CASCADE,
    archive_data bytea NOT NULL DEFAULT ''::bytea,
    archive_version integer,
    game_build_number integer,
    start_target bigint NOT NULL DEFAULT -3108528456028182417,
    aoi_volume integer NOT NULL DEFAULT 3200,
    gazillionite_balance bigint NOT NULL DEFAULT -1,
    last_logout_time bigint NOT NULL DEFAULT 0,
    revision bigint NOT NULL DEFAULT 0,
    created_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT player_profile_archive_version CHECK (archive_version IS NULL OR archive_version > 0),
    CONSTRAINT player_profile_game_build_number CHECK (game_build_number IS NULL OR game_build_number > 0),
    CONSTRAINT player_profile_revision CHECK (revision >= 0)
);

CREATE TABLE mhserveremu.player_entity (
    id bigint PRIMARY KEY,
    owner_account_id bigint NOT NULL REFERENCES mhserveremu.player_profile (account_id) ON DELETE CASCADE,
    kind integer NOT NULL,
    parent_entity_id bigint,
    inventory_proto_id bigint NOT NULL,
    slot bigint NOT NULL,
    entity_proto_id bigint NOT NULL,
    archive_data bytea NOT NULL DEFAULT ''::bytea,
    CONSTRAINT player_entity_owner_id_unique UNIQUE (owner_account_id, id),
    CONSTRAINT player_entity_kind CHECK (kind BETWEEN 0 AND 3),
    CONSTRAINT player_entity_slot CHECK (slot BETWEEN 0 AND 4294967295),
    CONSTRAINT player_entity_controlled_parent CHECK (kind <> 3 OR parent_entity_id IS NOT NULL),
    CONSTRAINT player_entity_parent FOREIGN KEY (owner_account_id, parent_entity_id) REFERENCES mhserveremu.player_entity (owner_account_id, id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX mhserveremu.player_entity_inventory_slot_unique ON mhserveremu.player_entity (owner_account_id, parent_entity_id, inventory_proto_id, slot) NULLS NOT DISTINCT WHERE inventory_proto_id <> 0;
CREATE INDEX mhserveremu.player_entity_owner_account_id_index ON mhserveremu.player_entity (owner_account_id);
CREATE INDEX mhserveremu.player_entity_owner_kind_parent_index ON mhserveremu.player_entity (owner_account_id, kind, parent_entity_id);

CREATE TABLE mhserveremu.guild (
    id bigint PRIMARY KEY,
    name text NOT NULL,
    normalized_name text NOT NULL,
    motd text NOT NULL,
    creator_account_id bigint NOT NULL,
    creation_time bigint NOT NULL,
    revision bigint NOT NULL DEFAULT 0,
    created_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT guild_revision CHECK (revision >= 0),
    CONSTRAINT guild_normalized_name_unique UNIQUE (normalized_name)
);

CREATE TABLE mhserveremu.guild_member (
    player_account_id bigint PRIMARY KEY REFERENCES mhserveremu.player_profile (account_id) ON DELETE CASCADE,
    guild_id bigint NOT NULL REFERENCES mhserveremu.guild (id) ON DELETE CASCADE,
    membership integer NOT NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT guild_member_membership CHECK (membership BETWEEN 1 AND 3)
);

CREATE UNIQUE INDEX mhserveremu.guild_member_one_leader_unique ON mhserveremu.guild_member (guild_id) WHERE membership = 3;
