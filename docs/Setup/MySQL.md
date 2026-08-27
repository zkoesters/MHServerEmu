# MySQL and MariaDB Setup

MySQL is an alternative account, player, and guild database backend. It is supported on MySQL 8.4 LTS and MariaDB 11.8 LTS. The database must exist before starting MHServerEmu; startup creates and migrates the MHServerEmu schema to version 6.

## Create the Database and Accounts

Run the following as a database administrator. Replace each placeholder before running the commands. The initialization account is used only when the server creates or upgrades the schema; it needs schema-migration privileges. The runtime account has only the data privileges MHServerEmu needs after the schema is current.

```sql
CREATE DATABASE mhserveremu CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

CREATE USER 'mhserveremu_init'@'your-server-host' IDENTIFIED BY 'your-initialization-password';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX ON mhserveremu.* TO 'mhserveremu_init'@'your-server-host';

CREATE USER 'mhserveremu'@'your-server-host' IDENTIFIED BY 'your-runtime-password';
GRANT SELECT, INSERT, UPDATE, DELETE ON mhserveremu.* TO 'mhserveremu'@'your-server-host';
```

Use the initialization account for the first server start. Once it completes, replace it with the runtime account in `ConfigOverride.ini`. Use the initialization account again only when upgrading MHServerEmu if startup must apply a schema migration. Do not use a MySQL/MariaDB administrator account for the server.

## Configure MHServerEmu

Keep connection details in `ConfigOverride.ini`, not `Config.ini` or source control. Replace every `your-...` placeholder with values for your deployment before starting the server.

```ini
[PlayerManager]
DatabaseType=MySQL
UseJsonDBManager=false

[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu_init;Password=your-initialization-password
```

Start the server with these initialization credentials. After startup creates or migrates the schema to version 6, replace the connection string with the runtime account and restart the server:

```ini
[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu;Password=your-runtime-password
```

`DatabaseType` can be `Json`, `SQLite`, `MySQL`, or `PostgreSQL`. `UseJsonDBManager` is a legacy setting; leave it `false` when selecting MySQL. Use `DatabaseType=MySQL` for both MySQL and MariaDB.

The connection string uses [MySqlConnector connection options](https://mysqlconnector.net/connection-options/). For production, use a CA certificate, verify the server certificate and hostname, and set explicit connection, command, and pooling limits:

```ini
[MySQLDBManager]
ConnectionString=Server=your-mysql-host;Port=3306;Database=mhserveremu;User ID=mhserveremu;Password=your-runtime-password;SslMode=VerifyFull;CACertificateFile=/path/to/your-ca-certificate.pem;ConnectionTimeout=15;DefaultCommandTimeout=30;Pooling=true;MinimumPoolSize=0;MaximumPoolSize=20
```

`SslMode=VerifyFull` validates the certificate chain and that `Server` matches the certificate hostname. `CACertificateFile` must point to the PEM CA certificate that issued the database server certificate when that CA is not trusted by the operating system. Do not use `SslMode=Preferred`, `SslMode=Disabled`, or any equivalent plaintext TLS setting for production account credentials.

## Operation and Backups

MySQL/MariaDB backups are not created by MHServerEmu. Use database operator tooling such as `mysqldump` and retain backups outside the server directory. Test restores regularly using your MySQL/MariaDB restore process.

Importing an existing SQLite `Data/Account.db` database into MySQL/MariaDB and synchronizing data between SQLite and MySQL/MariaDB are not supported. Start with a new MySQL/MariaDB database. When `DatabaseType=MySQL` is selected, MHServerEmu does not automatically fall back to SQLite if the configuration is invalid or the database cannot be reached.

## Integration Tests

MySQL/MariaDB integration tests are opt-in. Provision a dedicated operator account that can create and drop databases, then set `MHSERVEREMU_MYSQL_TEST_CONNECTION_STRING` to its connection string. The tests create and remove isolated databases; they do not create a reusable test database.

```bash
MHSERVEREMU_MYSQL_TEST_CONNECTION_STRING='Server=your-mysql-host;Port=3306;Database=your-operator-database;User ID=your-test-user;Password=your-test-password' dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Debug --filter "FullyQualifiedName~MHServerEmu.DatabaseAccess.Tests.MySQL"
```

Run the integration suite separately against MySQL 8.4 LTS and MariaDB 11.8 LTS. Do not point it at a production database or use production credentials.
