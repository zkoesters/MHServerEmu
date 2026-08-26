CREATE TABLE mhserveremu_schema (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    version INT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO mhserveremu_schema (id, version)
VALUES (1, 0);

CREATE TABLE account (
    id BIGINT PRIMARY KEY,
    email VARCHAR(320) COLLATE utf8mb4_unicode_ci NOT NULL,
    player_name VARCHAR(16) COLLATE utf8mb4_unicode_ci NOT NULL,
    password_hash BLOB NOT NULL,
    salt BLOB NOT NULL,
    user_level INT NOT NULL,
    is_banned INT NOT NULL,
    is_archived INT NOT NULL,
    is_password_expired INT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO account (
    id, email, player_name, password_hash, salt, user_level, is_banned, is_archived, is_password_expired
)
VALUES (1, 'legacy@example.com', 'LegacyPlayer', X'01', X'02', 0, 1, 0, 0);

CREATE TABLE player (
    id BIGINT PRIMARY KEY
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE avatar (
    id BIGINT PRIMARY KEY
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
