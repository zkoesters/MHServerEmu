CREATE TABLE mhserveremu_leaderboards_schema (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    version INT NOT NULL
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE leaderboard (
    leaderboard_id BIGINT PRIMARY KEY,
    prototype_name VARCHAR(255),
    active_instance_id BIGINT,
    is_enabled BOOLEAN,
    start_time BIGINT,
    max_reset_count INT
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE leaderboard_instance (
    instance_id BIGINT PRIMARY KEY,
    leaderboard_id BIGINT NOT NULL,
    state INT,
    activation_date BIGINT,
    visible BOOLEAN,
    KEY idx_instances_leaderboardid (leaderboard_id),
    CONSTRAINT fk_leaderboard_instance_leaderboard FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE leaderboard_entry (
    instance_id BIGINT NOT NULL,
    participant_id BIGINT NOT NULL,
    score BIGINT,
    high_score BIGINT,
    rule_states LONGBLOB,
    PRIMARY KEY (instance_id, participant_id),
    KEY idx_entries_instanceid (instance_id),
    CONSTRAINT fk_leaderboard_entry_instance FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE leaderboard_meta_entry (
    leaderboard_id BIGINT NOT NULL,
    instance_id BIGINT NOT NULL,
    sub_leaderboard_id BIGINT NOT NULL,
    sub_instance_id BIGINT NOT NULL,
    PRIMARY KEY (leaderboard_id, instance_id, sub_leaderboard_id),
    KEY idx_meta_entries_leaderboardid (leaderboard_id),
    CONSTRAINT fk_leaderboard_meta_entry_leaderboard FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE leaderboard_reward (
    leaderboard_id BIGINT NOT NULL,
    instance_id BIGINT NOT NULL,
    participant_id BIGINT NOT NULL,
    `rank` INT NOT NULL,
    reward_id BIGINT NOT NULL,
    creation_date BIGINT,
    rewarded_date BIGINT,
    PRIMARY KEY (leaderboard_id, instance_id, participant_id),
    KEY idx_rewards_instanceid (instance_id),
    KEY idx_rewards_participantid (participant_id),
    CONSTRAINT fk_leaderboard_reward_instance FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
