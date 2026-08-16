using System.Diagnostics;
using System.Text;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLMigrationRunnerTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLMigrationRunnerTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_FreshHistoryProbeReturnsNoRelation_AndBootstrapsFoundationAndCorePersistence()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            Assert.Null(await ScalarAsync<string>(dataSource, "SELECT to_regclass('mhserveremu.schema_migrations')::text"));

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.AppliedMigrationCount);
            Assert.Equal("mhserveremu.schema_migrations", await ScalarAsync<string>(dataSource, "SELECT to_regclass('mhserveremu.schema_migrations')::text"));
            Assert.Equal(new[] { "account", "application_metadata", "guild", "guild_member", "player_entity", "player_profile", "schema_migrations", "writer_fence" }, await TablesAsync(dataSource));
            Assert.Equal(new[] { 1, 2 }, await MigrationVersionsAsync(dataSource));
            Assert.Equal(1, await ScalarAsync<int>(dataSource, "SELECT identity_normalization_version FROM mhserveremu.application_metadata"));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT generation FROM mhserveremu.writer_fence WHERE singleton = true"));
            Assert.Equal(Guid.Empty, await ScalarAsync<Guid>(dataSource, "SELECT owner_id FROM mhserveremu.writer_fence WHERE singleton = true"));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_RootControlledEntity_ViolatesNamedParentConstraint()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            Assert.True((await new PostgreSQLMigrationRunner(dataSource, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync()).Succeeded);
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.account (id, email, normalized_email, player_name, normalized_player_name, password_hash, password_salt, user_level, flags) VALUES (1, 'player@example.com', 'player@example.com', 'Player', 'player', decode(repeat('00', 64), 'hex'), decode(repeat('00', 64), 'hex'), 0, 0)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.player_profile (account_id) VALUES (1)");

            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource, "INSERT INTO mhserveremu.player_entity (id, owner_account_id, kind, inventory_proto_id, slot, entity_proto_id) VALUES (1, 1, 3, 0, 0, 0)"));

            Assert.Equal("player_entity_controlled_parent", exception.ConstraintName);
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_AlreadyAppliedCatalog_IsIdempotent()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationRunner runner = new(dataSource, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
            Assert.True((await runner.RunAsync()).Succeeded);

            PostgreSQLMigrationResult result = await runner.RunAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.AppliedMigrationCount);
        }

        [PostgreSQLIntegrationTheory]
        [InlineData("checksum")]
        [InlineData("name")]
        public async Task RunAsync_HistoryDoesNotMatchCatalog_ReturnsFailure(string column)
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.LoadEmbedded();
            Assert.True((await new PostgreSQLMigrationRunner(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync()).Succeeded);
            await ExecuteAsync(dataSource, $"UPDATE mhserveremu.schema_migrations SET {column} = 'mismatch' WHERE version = 1");

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("MigrationHistoryMismatch", result.Failure.Code);
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_FutureHistoryVersion_ReturnsFailure()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.LoadEmbedded();
            Assert.True((await new PostgreSQLMigrationRunner(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync()).Succeeded);
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.schema_migrations VALUES (9999, 'Future', 'checksum', 'test', now(), 0)");

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("FutureMigration", result.Failure.Code);
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_FailedBatch_RollsBackAllMigrations()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = ExtendCatalog("SELECT * FROM missing_relation;");

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync();

            Assert.False(result.Succeeded);
            Assert.Null(await ScalarAsync<string>(dataSource, "SELECT to_regclass('mhserveremu.schema_migrations')::text"));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_ConcurrentRunners_ApplyTheCatalogOnce()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = ExtendCatalog("SELECT pg_sleep(0.2);");
            PostgreSQLMigrationRunner first = new(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
            PostgreSQLMigrationRunner second = new(dataSource, catalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

            PostgreSQLMigrationResult[] results = await Task.WhenAll(first.RunAsync(), second.RunAsync());

            Assert.All(results, result => Assert.True(result.Succeeded));
            Assert.Equal(2, results.Sum(result => result.AppliedMigrationCount));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_CompetingRunner_UsesTheEarlierMigrationLockDeadline()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = ExtendCatalog("SELECT pg_sleep(1);");
            PostgreSQLMigrationRunner first = new(dataSource, catalog, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
            Task<PostgreSQLMigrationResult> firstRun = first.RunAsync();
            await WaitForAdvisoryLockAsync(dataSource);
            PostgreSQLMigrationRunner second = new(dataSource, catalog, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            Stopwatch stopwatch = Stopwatch.StartNew();

            PostgreSQLMigrationResult secondResult = await second.RunAsync();
            stopwatch.Stop();

            Assert.False(secondResult.Succeeded);
            Assert.Equal("migration_lock_timeout", secondResult.Failure.Code);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
            Assert.True((await firstRun).Succeeded);
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_CompetingRunner_UsesTheEarlierMigrationDeadline()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog catalog = ExtendCatalog("SELECT pg_sleep(1);");
            PostgreSQLMigrationRunner first = new(dataSource, catalog, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
            Task<PostgreSQLMigrationResult> firstRun = first.RunAsync();
            await WaitForAdvisoryLockAsync(dataSource);
            PostgreSQLMigrationRunner second = new(dataSource, catalog, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
            Stopwatch stopwatch = Stopwatch.StartNew();

            PostgreSQLMigrationResult secondResult = await second.RunAsync();
            stopwatch.Stop();

            Assert.False(secondResult.Succeeded);
            Assert.Equal("migration_timeout", secondResult.Failure.Code);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            Assert.True((await firstRun).Succeeded);
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_LockedHistoryTable_ReturnsFailureAtTheLockDeadline()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationCatalog baseCatalog = PostgreSQLMigrationCatalog.LoadEmbedded();
            Assert.True((await new PostgreSQLMigrationRunner(dataSource, baseCatalog, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync()).Succeeded);
            await using NpgsqlConnection lockConnection = await dataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await lockConnection.BeginTransactionAsync();
            await using (NpgsqlCommand lockCommand = new("LOCK TABLE mhserveremu.schema_migrations IN ACCESS EXCLUSIVE MODE", lockConnection, transaction))
                await lockCommand.ExecuteNonQueryAsync();

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, ExtendCatalog("SELECT 1;"), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)).RunAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("MigrationFailed", result.Failure.Code);
        }

        private static PostgreSQLMigrationCatalog ExtendCatalog(string migrationSql)
        {
            PostgreSQLMigrationCatalog embedded = PostgreSQLMigrationCatalog.LoadEmbedded();
            return PostgreSQLMigrationCatalog.Create(embedded.Migrations
                .Select(migration => new PostgreSQLMigrationResource($"Migrations.{migration.Version:D4}_{migration.Name}.sql", Encoding.UTF8.GetBytes(migration.Sql)))
                .Append(new PostgreSQLMigrationResource("Migrations.0003_Test.sql", Encoding.UTF8.GetBytes(migrationSql))));
        }

        private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task WaitForAdvisoryLockAsync(NpgsqlDataSource dataSource)
        {
            using CancellationTokenSource cancellationSource = new(TimeSpan.FromSeconds(2));
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationSource.Token);
            while (true)
            {
                await using NpgsqlCommand command = new("SELECT pg_try_advisory_lock(0x4D485345, 2)", connection);
                if ((bool)await command.ExecuteScalarAsync(cancellationSource.Token) == false)
                    return;

                await using NpgsqlCommand unlockCommand = new("SELECT pg_advisory_unlock(0x4D485345, 2)", connection);
                await unlockCommand.ExecuteNonQueryAsync(cancellationSource.Token);
                await Task.Delay(10, cancellationSource.Token);
            }
        }

        private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            object result = await command.ExecuteScalarAsync();
            return result is DBNull ? default : (T)result;
        }

        private static async Task<string[]> TablesAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'mhserveremu' ORDER BY tablename");
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<string> tables = new();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
            return tables.ToArray();
        }

        private static async Task<int[]> MigrationVersionsAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT version FROM mhserveremu.schema_migrations ORDER BY version");
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<int> versions = new();
            while (await reader.ReadAsync())
                versions.Add(reader.GetInt32(0));
            return versions.ToArray();
        }
    }
}
