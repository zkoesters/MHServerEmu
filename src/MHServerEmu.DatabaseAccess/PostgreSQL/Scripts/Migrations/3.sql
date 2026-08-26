-- Remove the deprecated start target override column.
ALTER TABLE player DROP COLUMN start_target_region_override;

-- SQLite switched to WAL at this version; PostgreSQL requires no equivalent change.
