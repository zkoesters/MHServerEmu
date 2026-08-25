CREATE TABLE mhserveremu_leaderboards_schema (
    id smallint PRIMARY KEY CHECK (id = 1),
    version integer NOT NULL
);

CREATE TABLE leaderboard (
    leaderboard_id bigint PRIMARY KEY,
    prototype_name text,
    active_instance_id bigint,
    is_enabled boolean,
    start_time bigint,
    max_reset_count integer
);

CREATE TABLE leaderboard_instance (
    instance_id bigint PRIMARY KEY,
    leaderboard_id bigint NOT NULL,
    state integer,
    activation_date bigint,
    visible boolean,
    FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE
);

CREATE TABLE leaderboard_entry (
    instance_id bigint NOT NULL,
    participant_id bigint NOT NULL,
    score bigint,
    high_score bigint,
    rule_states bytea,
    PRIMARY KEY (instance_id, participant_id),
    FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE
);

CREATE TABLE leaderboard_meta_entry (
    leaderboard_id bigint NOT NULL,
    instance_id bigint NOT NULL,
    sub_leaderboard_id bigint NOT NULL,
    sub_instance_id bigint NOT NULL,
    PRIMARY KEY (leaderboard_id, instance_id, sub_leaderboard_id),
    FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE
);

CREATE TABLE leaderboard_reward (
    leaderboard_id bigint NOT NULL,
    instance_id bigint NOT NULL,
    participant_id bigint NOT NULL,
    rank integer NOT NULL,
    reward_id bigint NOT NULL,
    creation_date bigint,
    rewarded_date bigint NULL,
    PRIMARY KEY (leaderboard_id, instance_id, participant_id),
    FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE
);

CREATE INDEX idx_instances_leaderboardid ON leaderboard_instance (leaderboard_id);
CREATE INDEX idx_entries_instanceid ON leaderboard_entry (instance_id);
CREATE INDEX idx_rewards_participantid ON leaderboard_reward (participant_id);

INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (1, 1);
