CREATE TABLE mhserveremu_schema (
    id smallint PRIMARY KEY CHECK (id = 1),
    version integer NOT NULL
);

INSERT INTO mhserveremu_schema (id, version)
VALUES (1, 0);

CREATE TABLE account (
    id bigint PRIMARY KEY,
    email text NOT NULL,
    player_name text NOT NULL,
    password_hash bytea NOT NULL,
    salt bytea NOT NULL,
    user_level integer NOT NULL,
    is_banned integer NOT NULL,
    is_archived integer NOT NULL,
    is_password_expired integer NOT NULL
);

INSERT INTO account (
    id, email, player_name, password_hash, salt, user_level, is_banned, is_archived, is_password_expired
)
VALUES (1, 'legacy@example.com', 'LegacyPlayer', '\x01', '\x02', 0, 1, 0, 0);

CREATE TABLE player (
    id bigint PRIMARY KEY
);

CREATE TABLE avatar (
    id bigint PRIMARY KEY
);
