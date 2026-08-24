-- Add Flags column to the player table.
ALTER TABLE player ADD COLUMN flags bigint NOT NULL DEFAULT 0;
