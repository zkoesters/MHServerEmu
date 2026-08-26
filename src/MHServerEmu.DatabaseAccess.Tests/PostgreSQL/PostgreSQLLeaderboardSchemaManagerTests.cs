using MHServerEmu.DatabaseAccess.PostgreSQL;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLLeaderboardSchemaManagerTests
    {
        [PostgreSQLFact]
        public void EnsureCurrentSchema_FreshDatabase_CreatesVersionOneLeaderboardSchemaInIsolation()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();

            PostgreSQLLeaderboardSchemaResult result = PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            Assert.True(result.Created);
            Assert.Equal(1, result.Version);
            Assert.Equal(1, GetMetadataVersion(connection));
            Assert.True(HasTable(connection, "leaderboard"));
            Assert.True(HasTable(connection, "leaderboard_instance"));
            Assert.True(HasTable(connection, "leaderboard_entry"));
            Assert.True(HasTable(connection, "leaderboard_meta_entry"));
            Assert.True(HasTable(connection, "leaderboard_reward"));
            Assert.False(HasTable(connection, "mhserveremu_schema"));
            Assert.Equal(3, GetLeaderboardIndexCount(connection));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_CurrentSchema_ReturnsExistingVersion()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            PostgreSQLLeaderboardSchemaResult result = PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            Assert.False(result.Created);
            Assert.Equal(1, result.Version);
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_EmptyMetadata_RejectsWithoutCreatingLeaderboardTables()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE mhserveremu_leaderboards_schema (id smallint, version integer)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(HasTable(connection, "leaderboard"));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_MetadataWithWrongId_RejectsWithoutChangingMetadata()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_leaderboards_schema DROP CONSTRAINT mhserveremu_leaderboards_schema_pkey");
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_leaderboards_schema DROP CONSTRAINT mhserveremu_leaderboards_schema_id_check");
            ExecuteNonQuery(connection, "UPDATE mhserveremu_leaderboards_schema SET id = 2");

            Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(2, GetMetadataId(connection));
            Assert.Equal(1, GetMetadataVersion(connection, 2));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_DuplicateMetadataRows_RejectsWithoutChangingRows()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_leaderboards_schema DROP CONSTRAINT mhserveremu_leaderboards_schema_pkey");
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_leaderboards_schema DROP CONSTRAINT mhserveremu_leaderboards_schema_id_check");
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (2, 1)");

            Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(2, GetMetadataRowCount(connection));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_NewerMetadataVersion_RejectsWithoutChangingVersion()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "UPDATE mhserveremu_leaderboards_schema SET version = 2 WHERE id = 1");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, GetMetadataVersion(connection));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_KnownTableWithoutMetadata_RejectsWithoutDroppingTable()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE leaderboard (leaderboard_id bigint PRIMARY KEY)");

            Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.True(HasTable(connection, "leaderboard"));
            Assert.False(HasTable(connection, "mhserveremu_leaderboards_schema"));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_InitializationCallbackFails_RollsBackSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();

            Assert.Throws<InvalidOperationException>(() => PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(
                connection,
                (_, _) => throw new InvalidOperationException("Initialization callback failed.")));

            Assert.False(HasTable(connection, "mhserveremu_leaderboards_schema"));
            Assert.Equal(0, GetKnownLeaderboardTableCount(connection));
        }

        [PostgreSQLFact]
        public async Task EnsureCurrentSchema_ConcurrentInitialization_CreatesExactlyOnce()
        {
            using PostgreSQLTestDatabase database = new();
            using Barrier barrier = new(2);
            Task<PostgreSQLLeaderboardSchemaResult> first = Task.Run(() => InitializeAfterBarrier(database.ConnectionString, barrier));
            Task<PostgreSQLLeaderboardSchemaResult> second = Task.Run(() => InitializeAfterBarrier(database.ConnectionString, barrier));

            PostgreSQLLeaderboardSchemaResult[] results = await Task.WhenAll(first, second);

            Assert.Single(results.Where(result => result.Created));
            Assert.Single(results.Where(result => result.Created == false));
            Assert.All(results, result => Assert.Equal(1, result.Version));
        }

        private static PostgreSQLLeaderboardSchemaResult InitializeAfterBarrier(string connectionString, Barrier barrier)
        {
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
            barrier.SignalAndWait();
            return PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
        }

        private static int GetKnownLeaderboardTableCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new(@"
                SELECT COUNT(*)
                FROM unnest(ARRAY['leaderboard', 'leaderboard_instance', 'leaderboard_entry',
                    'leaderboard_meta_entry', 'leaderboard_reward']) AS table_name
                WHERE to_regclass(table_name) IS NOT NULL", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetLeaderboardIndexCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new(@"
                SELECT COUNT(*)
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname IN ('idx_instances_leaderboardid', 'idx_entries_instanceid', 'idx_rewards_participantid')", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataId(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT id FROM mhserveremu_leaderboards_schema", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataRowCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM mhserveremu_leaderboards_schema", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataVersion(NpgsqlConnection connection, short id = 1)
        {
            using NpgsqlCommand command = new("SELECT version FROM mhserveremu_leaderboards_schema WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static bool HasTable(NpgsqlConnection connection, string tableName)
        {
            using NpgsqlCommand command = new("SELECT to_regclass(@tableName) IS NOT NULL", connection);
            command.Parameters.AddWithValue("tableName", tableName);
            return (bool)command.ExecuteScalar();
        }

        private static void ExecuteNonQuery(NpgsqlConnection connection, string commandText)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }
    }
}
