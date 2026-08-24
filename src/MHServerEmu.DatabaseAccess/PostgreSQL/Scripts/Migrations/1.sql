-- Convert legacy account state columns into the shared flags field.
ALTER TABLE account RENAME COLUMN is_banned TO flags;
ALTER TABLE account DROP COLUMN is_archived;
ALTER TABLE account DROP COLUMN is_password_expired;
