-- Add the per-account Gazillionite balance.
ALTER TABLE player ADD COLUMN gazillionite_balance bigint;
UPDATE player SET gazillionite_balance=-1;
