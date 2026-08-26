# MySQL and MariaDB Setup

MySQL is an alternative account, player, guild, and leaderboard database backend. It is supported on MySQL 8.4 LTS, MySQL 9.7, MariaDB 11.8 LTS, and MariaDB 12.3. The database must exist before starting MHServerEmu; startup creates and migrates the MHServerEmu schema.

## Create the Database and Accounts

Run the following as a database administrator. Replace each placeholder before running the commands. The initialization account is used only when the server creates or upgrades the schema; it needs schema-migration privileges. The runtime account has only the data privileges MHServerEmu needs after the schema is current.

```sql
CREATE DATABASE mhserveremu CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

CREATE USER 'mhserveremu_init'@'your-server-host' IDENTIFIED BY 'your-initialization-password';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON mhserveremu.* TO 'mhserveremu_init'@'your-server-host';

CREATE USER 'mhserveremu'@'your-server-host' IDENTIFIED BY 'your-runtime-password';
GRANT SELECT, INSERT, UPDATE, DELETE ON mhserveremu.* TO 'mhserveremu'@'your-server-host';
```

Use the initialization account for the first server start. Once it completes, replace it with the runtime account in `ConfigOverride.ini`. Use the initialization account again only when upgrading MHServerEmu if startup must apply a schema migration. Do not use a MySQL/MariaDB administrator account for the server.

## Configure MHServerEmu

Keep connection details in `ConfigOverride.ini`, not `Config.ini` or source control. Replace every `your-...` placeholder with values for your deployment before starting the server. Use the initialization account for the first startup so MHServerEmu can create the schema:

```ini
[PlayerManager]
DatabaseType=MySQL
UseJsonDBManager=false

[Leaderboards]
DatabaseType=MySQL

[GameOptions]
LeaderboardsEnabled=true

[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu_init;Password=your-initialization-password
```

After the schema is current, replace the connection string with the restricted runtime account:

```ini
[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu;Password=your-runtime-password
```

`PlayerManager.DatabaseType` and `Leaderboards.DatabaseType` are independent selectors. Each can select `MySQL` without changing the other; both use the shared `[MySQLDBManager]` connection string. `UseJsonDBManager` is a legacy PlayerManager setting; leave it `false` when selecting MySQL. Use `DatabaseType=MySQL` for both MySQL and MariaDB. `Leaderboards.DatabaseFile` applies only when `Leaderboards.DatabaseType=SQLite` and is ignored for MySQL. Set `GameOptions.LeaderboardsEnabled=true` to enable leaderboard operation.

MySQL keeps account, player, guild, and leaderboard data in the configured database. The independent MySQL leaderboard schema is version 1. Leaderboard tables are an independent schema scope within that database, so selecting MySQL for leaderboards does not require selecting MySQL for PlayerManager.

The connection string uses [MySqlConnector connection options](https://mysqlconnector.net/connection-options/). For production, use a CA certificate, verify the server certificate and hostname, and set explicit connection, command, and pooling limits:

```ini
[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu;Password=your-runtime-password;SslMode=VerifyFull;CACertificateFile=/path/to/your-ca-certificate.pem;ConnectionTimeout=15;DefaultCommandTimeout=30;Pooling=true;MinimumPoolSize=0;MaximumPoolSize=20
```

`SslMode=VerifyFull` validates the certificate chain and that `Server` matches the certificate hostname. `CACertificateFile` must point to the PEM CA certificate that issued the database server certificate when that CA is not trusted by the operating system. Do not use `SslMode=Preferred`, `SslMode=Disabled`, or any equivalent plaintext TLS setting for production account credentials.

## Operation and Backups

MySQL/MariaDB backups are not created by MHServerEmu. Use database operator tooling such as `mysqldump` for the complete database, including the independent leaderboard tables, and retain backups outside the server directory. Test restores regularly using your MySQL/MariaDB restore process; restore the complete database before starting MHServerEmu.

Importing existing SQLite account or leaderboard databases into MySQL/MariaDB and synchronizing MySQL/MariaDB with SQLite are not supported. Start with a new MySQL/MariaDB database. When either database selector is set to `MySQL`, MHServerEmu does not automatically fall back to SQLite if the configuration is invalid or the database cannot be reached.

Leaderboard scheduling is supported for one MHServerEmu process at a time. Do not run multiple server processes against the same MySQL leaderboard database because scheduling has no distributed coordination.

## Integration Tests

MySQL/MariaDB integration tests are opt-in. Provision a dedicated operator account that can create and drop databases, then set `MHSERVEREMU_MYSQL_TEST_CONNECTION_STRING` to its connection string. The tests create and remove isolated databases; they do not create a reusable test database.

```bash
MHSERVEREMU_MYSQL_TEST_CONNECTION_STRING='Server=your-mysql-host;Port=3306;Database=your-operator-database;User ID=your-test-user;Password=your-test-password' dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration "Debug 1.53" -p:Platform=x64 --filter "FullyQualifiedName~MHServerEmu.DatabaseAccess.Tests.MySQL"
```

Run the integration suite separately against MySQL 8.4 LTS, MySQL 9.7, MariaDB 11.8 LTS, and MariaDB 12.3. Do not point it at a production database or use production credentials.
