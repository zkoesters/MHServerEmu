-- Remove the deprecated start target override column.
ALTER TABLE player DROP COLUMN start_target_region_override;

-- MySQL and MariaDB require no equivalent change.
