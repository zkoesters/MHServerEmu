# PostgreSQL Setup

PostgreSQL is an alternative account database backend. The database must exist before starting MHServerEmu. Startup creates and migrates the MHServerEmu schema.

## Create the Database

Run the following as a PostgreSQL administrator. Replace `your-password` before running the commands.

```sql
CREATE ROLE mhserveremu LOGIN PASSWORD 'your-password';
CREATE DATABASE mhserveremu OWNER mhserveremu;
\connect mhserveremu
GRANT USAGE, CREATE ON SCHEMA public TO mhserveremu;
```

The role must be able to connect to the database and create schema objects. Do not use the PostgreSQL administrator role for the server.

## Configure MHServerEmu

Keep connection details in `ConfigOverride.ini`, not `Config.ini` or source control. Replace every `your-...` placeholder with values for your PostgreSQL deployment before starting the server.

```ini
[PlayerManager]
DatabaseType=PostgreSQL
UseJsonDBManager=false

[PostgreSQLDBManager]
ConnectionString=Host=your-postgres-host;Port=5432;Database=mhserveremu;Username=mhserveremu;Password=your-password
```

`DatabaseType` can be `Json`, `SQLite`, or `PostgreSQL`. `UseJsonDBManager` is a legacy setting; leave it `false` when selecting PostgreSQL.

The connection string uses [Npgsql connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html). For a TLS-protected deployment with explicit connection and command timeouts and Npgsql pooling, use options such as the following:

```ini
[PostgreSQLDBManager]
ConnectionString=Host=your-postgres-host;Port=5432;Database=mhserveremu;Username=mhserveremu;Password=your-password;SSL Mode=VerifyFull;Root Certificate=/path/to/your-root-certificate.crt;Timeout=15;Command Timeout=30;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=20
```

`SSL Mode=VerifyFull` validates both the certificate chain and hostname. Use a certificate and hostname that match `Host`; do not disable certificate validation in production. MHServerEmu opens connections as needed, and Npgsql returns disposed connections to its pool.

## Operation and Backups

Run only one MHServerEmu process against a PostgreSQL database. Multiple server processes sharing the same database are unsupported.

PostgreSQL backups are not created by MHServerEmu. Use PostgreSQL operator tooling such as `pg_dump` and retain backups outside the server directory:

```bash
pg_dump --host=your-postgres-host --port=5432 --username=mhserveremu --format=custom --file=mhserveremu-$(date +%F).dump mhserveremu
```

Configure authentication through PostgreSQL operator controls, such as a protected `.pgpass` file or the interactive password prompt. Test restores regularly with `pg_restore`.

PostgreSQL leaderboard persistence is not supported. Importing an existing SQLite `Data/Account.db` database into PostgreSQL is also not supported; start with a new PostgreSQL database.

## Integration Tests

PostgreSQL integration tests are opt-in. Provision and use a dedicated test database, such as `mhserveremu_test`. Set `MHSERVEREMU_POSTGRES_TEST_CONNECTION_STRING` to its connection string, replacing every placeholder before use. The test role needs `CREATE` on that database and must be able to create and drop schemas.

```bash
MHSERVEREMU_POSTGRES_TEST_CONNECTION_STRING='Host=your-postgres-host;Port=5432;Database=mhserveremu_test;Username=your-test-user;Password=your-password' dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Debug
```

The test suite creates and removes isolated schemas through that connection; it does not create or delete databases. Do not point it at a production database or use production credentials.
