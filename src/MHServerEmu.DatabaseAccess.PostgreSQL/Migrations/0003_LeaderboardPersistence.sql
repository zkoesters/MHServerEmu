CREATE TABLE mhserveremu.leaderboard (
    leaderboard_id bigint PRIMARY KEY,
    prototype_name text NOT NULL,
    active_instance_id bigint,
    is_enabled boolean NOT NULL,
    start_time bigint NOT NULL,
    max_reset_count integer NOT NULL,
    CONSTRAINT leaderboard_max_reset_count CHECK (max_reset_count >= 0)
);

CREATE TABLE mhserveremu.leaderboard_instance (
    instance_id bigint PRIMARY KEY,
    leaderboard_id bigint NOT NULL,
    state smallint NOT NULL,
    activation_date bigint NOT NULL,
    visible boolean NOT NULL,
    CONSTRAINT leaderboard_instance_leaderboard_instance_unique UNIQUE (leaderboard_id, instance_id),
    CONSTRAINT leaderboard_instance_state CHECK (state BETWEEN 0 AND 5),
    CONSTRAINT leaderboard_instance_leaderboard FOREIGN KEY (leaderboard_id) REFERENCES mhserveremu.leaderboard (leaderboard_id) ON DELETE CASCADE
);

ALTER TABLE mhserveremu.leaderboard
    ADD CONSTRAINT leaderboard_active_instance
    FOREIGN KEY (leaderboard_id, active_instance_id)
    REFERENCES mhserveremu.leaderboard_instance (leaderboard_id, instance_id)
    DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE mhserveremu.leaderboard_entry (
    instance_id bigint NOT NULL,
    participant_id bigint NOT NULL,
    score bigint NOT NULL,
    high_score bigint NOT NULL,
    rule_states bytea NOT NULL,
    PRIMARY KEY (instance_id, participant_id),
    CONSTRAINT leaderboard_entry_instance FOREIGN KEY (instance_id) REFERENCES mhserveremu.leaderboard_instance (instance_id) ON DELETE CASCADE
);

CREATE TABLE mhserveremu.leaderboard_meta_entry (
    leaderboard_id bigint NOT NULL,
    instance_id bigint NOT NULL,
    sub_leaderboard_id bigint NOT NULL,
    sub_instance_id bigint NOT NULL,
    PRIMARY KEY (leaderboard_id, instance_id, sub_leaderboard_id),
    CONSTRAINT leaderboard_meta_entry_parent_instance FOREIGN KEY (leaderboard_id, instance_id) REFERENCES mhserveremu.leaderboard_instance (leaderboard_id, instance_id) ON DELETE CASCADE,
    CONSTRAINT leaderboard_meta_entry_sub_instance FOREIGN KEY (sub_leaderboard_id, sub_instance_id) REFERENCES mhserveremu.leaderboard_instance (leaderboard_id, instance_id) ON DELETE CASCADE
);

CREATE TABLE mhserveremu.leaderboard_reward (
    leaderboard_id bigint NOT NULL,
    instance_id bigint NOT NULL,
    participant_id bigint NOT NULL,
    reward_id bigint NOT NULL,
    rank integer NOT NULL,
    creation_date bigint NOT NULL,
    rewarded_date bigint,
    PRIMARY KEY (leaderboard_id, instance_id, participant_id),
    CONSTRAINT leaderboard_reward_rank CHECK (rank > 0),
    CONSTRAINT leaderboard_reward_instance FOREIGN KEY (leaderboard_id, instance_id) REFERENCES mhserveremu.leaderboard_instance (leaderboard_id, instance_id) ON DELETE CASCADE
);

CREATE INDEX leaderboard_instance_lifecycle_index ON mhserveremu.leaderboard_instance (leaderboard_id, state, visible, instance_id);
CREATE INDEX leaderboard_entry_instance_high_score_index ON mhserveremu.leaderboard_entry (instance_id, high_score);
CREATE INDEX leaderboard_meta_entry_parent_index ON mhserveremu.leaderboard_meta_entry (leaderboard_id, instance_id);
CREATE INDEX leaderboard_reward_pending_participant_index ON mhserveremu.leaderboard_reward (participant_id) WHERE rewarded_date IS NULL;
