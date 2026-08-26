-- Convert legacy account state columns into the shared flags field.
ALTER TABLE account CHANGE COLUMN is_banned flags INT NOT NULL;
ALTER TABLE account DROP COLUMN is_archived;
ALTER TABLE account DROP COLUMN is_password_expired;
