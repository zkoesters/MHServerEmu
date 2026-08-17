using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLLeaderboardMigrationTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLLeaderboardMigrationTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_LeaderboardPersistenceCreatesExpectedColumnsAndIndexes()
        {
            NpgsqlDataSource dataSource = await MigrateAsync();

            Assert.Equal(new[]
            {
                "leaderboard_id:bigint:NO", "prototype_name:text:NO", "active_instance_id:bigint:YES", "is_enabled:boolean:NO", "start_time:bigint:NO", "max_reset_count:integer:NO",
                "instance_id:bigint:NO", "leaderboard_id:bigint:NO", "state:smallint:NO", "activation_date:bigint:NO", "visible:boolean:NO",
                "instance_id:bigint:NO", "participant_id:bigint:NO", "score:bigint:NO", "high_score:bigint:NO", "rule_states:bytea:NO",
                "leaderboard_id:bigint:NO", "instance_id:bigint:NO", "sub_leaderboard_id:bigint:NO", "sub_instance_id:bigint:NO",
                "leaderboard_id:bigint:NO", "instance_id:bigint:NO", "participant_id:bigint:NO", "reward_id:bigint:NO", "rank:integer:NO", "creation_date:bigint:NO", "rewarded_date:bigint:YES",
            }, await ColumnsAsync(dataSource));

            Assert.Equal("leaderboard_instance_lifecycle_index", await IndexNameAsync(dataSource, "leaderboard_instance", "leaderboard_id, state, visible, instance_id"));
            Assert.Equal("leaderboard_entry_instance_high_score_index", await IndexNameAsync(dataSource, "leaderboard_entry", "instance_id, high_score"));
            Assert.Equal("leaderboard_meta_entry_parent_index", await IndexNameAsync(dataSource, "leaderboard_meta_entry", "leaderboard_id, instance_id"));
            Assert.Equal("leaderboard_reward_pending_participant_index", await IndexNameAsync(dataSource, "leaderboard_reward", "participant_id"));
            Assert.Contains("WHERE (rewarded_date IS NULL)", await IndexDefinitionAsync(dataSource, "leaderboard_reward_pending_participant_index"));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_LeaderboardPersistenceEnforcesOwnershipCascadesAndDeferredActivePointer()
        {
            NpgsqlDataSource dataSource = await MigrateAsync();
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard (leaderboard_id, prototype_name, is_enabled, start_time, max_reset_count) VALUES (1, 'one', true, 0, 0), (2, 'two', true, 0, 0)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible) VALUES (11, 1, 0, 0, true), (22, 2, 0, 0, true)");

            PostgresException mismatch = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_meta_entry (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id) VALUES (1, 11, 2, 11)"));
            Assert.Equal("leaderboard_meta_entry_sub_instance", mismatch.ConstraintName);

            await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync())
            await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, transaction, "UPDATE mhserveremu.leaderboard SET active_instance_id = 12 WHERE leaderboard_id = 1");
                await ExecuteAsync(connection, transaction, "INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible) VALUES (12, 1, 0, 0, true)");
                await transaction.CommitAsync();
            }

            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_entry (instance_id, participant_id, score, high_score, rule_states) VALUES (12, 99, 100, 100, decode('', 'hex'))");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_meta_entry (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id) VALUES (1, 12, 2, 22)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_reward (leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date) VALUES (1, 12, 99, 1, 1, 0)");
            await ExecuteAsync(dataSource, "DELETE FROM mhserveremu.leaderboard WHERE leaderboard_id = 1");
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_instance WHERE leaderboard_id = 1"));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_entry WHERE instance_id = 12"));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_meta_entry WHERE leaderboard_id = 1"));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_reward WHERE leaderboard_id = 1"));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_LeaderboardPersistenceCascadesMetaEntryWhenReferencedSubInstanceIsDeleted()
        {
            NpgsqlDataSource dataSource = await MigrateAsync();
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard (leaderboard_id, prototype_name, is_enabled, start_time, max_reset_count) VALUES (1, 'parent', true, 0, 0), (2, 'sub', true, 0, 0)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible) VALUES (11, 1, 0, 0, true), (22, 2, 0, 0, true)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_meta_entry (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id) VALUES (1, 11, 2, 22)");

            await ExecuteAsync(dataSource, "DELETE FROM mhserveremu.leaderboard_instance WHERE instance_id = 22");

            Assert.Equal(1L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_instance WHERE instance_id = 11"));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard WHERE leaderboard_id = 1"));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM mhserveremu.leaderboard_meta_entry WHERE leaderboard_id = 1 AND instance_id = 11"));
        }

        [PostgreSQLIntegrationFact]
        public async Task RunAsync_LeaderboardPersistenceRejectsCrossLeaderboardPointerMalformedStateAndRank()
        {
            NpgsqlDataSource dataSource = await MigrateAsync();
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard (leaderboard_id, prototype_name, is_enabled, start_time, max_reset_count) VALUES (1, 'one', true, 0, 0), (2, 'two', true, 0, 0)");
            await ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible) VALUES (11, 1, 0, 0, true), (22, 2, 0, 0, true)");

            await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync())
            await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, transaction, "UPDATE mhserveremu.leaderboard SET active_instance_id = 22 WHERE leaderboard_id = 1");
                PostgresException pointer = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                Assert.Equal("leaderboard_active_instance", pointer.ConstraintName);
            }

            PostgresException state = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible) VALUES (12, 1, 6, 0, true)"));
            Assert.Equal("leaderboard_instance_state", state.ConstraintName);
            PostgresException resetCount = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard (leaderboard_id, prototype_name, is_enabled, start_time, max_reset_count) VALUES (3, 'three', true, 0, -1)"));
            Assert.Equal("leaderboard_max_reset_count", resetCount.ConstraintName);
            PostgresException rank = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource, "INSERT INTO mhserveremu.leaderboard_reward (leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date) VALUES (1, 11, 99, 1, 0, 0)"));
            Assert.Equal("leaderboard_reward_rank", rank.ConstraintName);
        }

        private async Task<NpgsqlDataSource> MigrateAsync()
        {
            NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)).RunAsync();
            Assert.True(result.Succeeded, result.Failure?.ToString());
            return dataSource;
        }

        private static async Task<string[]> ColumnsAsync(NpgsqlDataSource dataSource)
        {
            const string sql = """
                SELECT column_name || ':' || data_type || ':' || is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'mhserveremu'
                  AND table_name IN ('leaderboard', 'leaderboard_instance', 'leaderboard_entry', 'leaderboard_meta_entry', 'leaderboard_reward')
                ORDER BY CASE table_name WHEN 'leaderboard' THEN 1 WHEN 'leaderboard_instance' THEN 2 WHEN 'leaderboard_entry' THEN 3 WHEN 'leaderboard_meta_entry' THEN 4 ELSE 5 END, ordinal_position
                """;
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<string> columns = new();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
            return columns.ToArray();
        }

        private static async Task<string> IndexNameAsync(NpgsqlDataSource dataSource, string table, string columns)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT indexname FROM pg_indexes WHERE schemaname = 'mhserveremu' AND tablename = @table AND indexdef LIKE @columns");
            command.Parameters.AddWithValue("table", table);
            command.Parameters.AddWithValue("columns", $"%({columns})%");
            return (string)await command.ExecuteScalarAsync();
        }

        private static async Task<string> IndexDefinitionAsync(NpgsqlDataSource dataSource, string indexName)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = 'mhserveremu' AND indexname = @indexName");
            command.Parameters.AddWithValue("indexName", indexName);
            return (string)await command.ExecuteScalarAsync();
        }

        private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            return (T)await command.ExecuteScalarAsync();
        }
    }
}
