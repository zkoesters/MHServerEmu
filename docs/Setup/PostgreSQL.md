# PostgreSQL Setup

PostgreSQL is supported for fresh MHServerEmu installations. It does not import or convert existing JSON or SQLite data, and there is no downgrade path from PostgreSQL to another provider. One running server process may write to a database at a time. Application and schema upgrades are supported through the ordered migrations that run during server startup.

## Create the Database

Create a dedicated role and database. Run the password command interactively so a secret is never stored in a shell history or configuration file.

```sql
CREATE ROLE mhserveremu LOGIN;
\password mhserveremu
CREATE DATABASE mhserveremu OWNER mhserveremu;
```

The database must remain owned by `mhserveremu`; do not run the server as a PostgreSQL superuser. Configure the server in `ConfigOverride.ini` only:

```ini
[Persistence]
Provider=PostgreSQL

[PostgreSQL]
ConnectionString=
```

Set the empty `ConnectionString` value in `ConfigOverride.ini` to `Host=db.example.invalid;Port=5432;Database=mhserveremu;Username=mhserveremu;SSL Mode=VerifyFull;Root Certificate=/etc/mhserveremu/postgresql-root.crt`. Use a DNS name that matches the server certificate when `SSL Mode=VerifyFull` is enabled. Do not add `ConnectionString` to `Config.ini`, commit `ConfigOverride.ini`, or use a JSON/SQLite database file as an input to PostgreSQL.

Protect the override file on Unix:

```bash
chmod 600 ConfigOverride.ini
```

Protect it on Windows in an elevated PowerShell session, replacing the account name if the service runs as another user:

```powershell
icacls ConfigOverride.ini /inheritance:r /grant:r "$env:USERNAME:(R,W)"
```

## Back Up and Restore

Stop MHServerEmu before a backup or restore so the single-writer rule is maintained. Create a portable backup without original ownership metadata:

```bash
pg_dump --format=custom --no-owner --file=mhserveremu.backup mhserveremu
```

Restore only into a clean target database owned by the server role:

```sql
CREATE DATABASE mhserveremu_restore OWNER mhserveremu;
```

```bash
pg_restore --no-owner --dbname=mhserveremu_restore mhserveremu.backup
```

Verify the target ownership and migration history before pointing the server at it:

```sql
SELECT datname, pg_get_userbyid(datdba) AS owner
FROM pg_database
WHERE datname IN ('mhserveremu', 'mhserveremu_restore');

SELECT tableowner
FROM pg_tables
WHERE schemaname = 'mhserveremu' AND tablename = 'schema_migrations';

SELECT version, name
FROM mhserveremu.schema_migrations
ORDER BY version;
```

## Upgrade

For each PostgreSQL application upgrade, stop the server, create and verify a backup, deploy the new application, start one server process, and verify startup completed its ordered migrations. If startup migration or verification fails, stop the server and restore the verified backup to a clean database. Do not attempt an in-place conversion to JSON/SQLite or a PostgreSQL downgrade.
