using System.Reflection;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLSchemaManagerTests
    {
        [PostgreSQLFact]
        public void EnsureCurrentSchema_FreshDatabase_InitializesCurrentSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();

            PostgreSQLSchemaResult result = PostgreSQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.True(result.Created);
            Assert.Equal(7, result.Version);
            Assert.Equal(7, GetSchemaVersion(connection));
            Assert.Equal(8, GetKnownApplicationTableCount(connection));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_CurrentDatabase_ReturnsExistingSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLSchemaManager.EnsureCurrentSchema(connection);

            PostgreSQLSchemaResult result = PostgreSQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.False(result.Created);
            Assert.Equal(7, result.Version);
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_NewerMetadataVersion_RejectsWithoutChangingVersion()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "UPDATE mhserveremu_schema SET version = 8 WHERE id = 1");
            ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (42, 'schema-marker@example.com', 'SchemaMarker', '\x01', '\x02', 0, 0)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PostgreSQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(8, GetSchemaVersion(connection));
            Assert.Equal(1, GetAccountCount(connection));
            Assert.Equal("schema-marker@example.com", GetAccountEmail(connection, 42));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_MetadataWithExtraRow_RejectsWithoutChangingMetadata()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            PostgreSQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_schema DROP CONSTRAINT mhserveremu_schema_pkey");
            ExecuteNonQuery(connection, "ALTER TABLE mhserveremu_schema DROP CONSTRAINT mhserveremu_schema_id_check");
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_schema (id, version) VALUES (2, 7)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PostgreSQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, GetMetadataRowCount(connection));
            Assert.Equal(7, GetSchemaVersion(connection));
            Assert.Equal(7, GetMetadataVersion(connection, 2));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_ApplicationTableWithoutMetadata_RejectsWithoutDroppingTable()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE account (id bigint PRIMARY KEY)");

            Assert.Throws<InvalidOperationException>(() => PostgreSQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.True(HasTable(connection, "account"));
            Assert.False(HasTable(connection, "mhserveremu_schema"));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_InitializationCallbackFails_RollsBackSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();

            Assert.Throws<InvalidOperationException>(() => PostgreSQLSchemaManager.EnsureCurrentSchema(
                connection,
                (_, _) => throw new InvalidOperationException("Initialization callback failed.")));

            Assert.False(HasTable(connection, "mhserveremu_schema"));
            Assert.Equal(0, GetKnownApplicationTableCount(connection));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_VersionZeroDatabase_MigratesToCurrentSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            PostgreSQLSchemaResult result = PostgreSQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.False(result.Created);
            Assert.Equal(7, result.Version);
            Assert.Equal(7, GetSchemaVersion(connection));
            Assert.Equal(1, GetAccountFlags(connection, 1));
            Assert.True(HasTable(connection, "player"));
            Assert.True(HasTable(connection, "avatar"));
            Assert.True(HasTable(connection, "team_up"));
            Assert.True(HasTable(connection, "item"));
            Assert.True(HasTable(connection, "controlled_entity"));
            Assert.True(HasTable(connection, "guild"));
            Assert.True(HasTable(connection, "guild_member"));
            AssertHasColumns(connection, "account", "id", "email", "player_name", "password_hash", "salt", "user_level", "flags");
            AssertHasColumns(connection, "player", "db_guid", "archive_data", "start_target", "aoi_volume", "gazillionite_balance", "last_logout_time", "flags");
            AssertHasColumns(connection, "avatar", "db_guid", "container_db_guid", "inventory_proto_guid", "slot", "entity_proto_guid", "archive_data");
            AssertHasColumns(connection, "team_up", "db_guid", "container_db_guid", "inventory_proto_guid", "slot", "entity_proto_guid", "archive_data");
            AssertHasColumns(connection, "item", "db_guid", "container_db_guid", "inventory_proto_guid", "slot", "entity_proto_guid", "archive_data");
            AssertHasColumns(connection, "controlled_entity", "db_guid", "container_db_guid", "inventory_proto_guid", "slot", "entity_proto_guid", "archive_data");
            AssertHasColumns(connection, "guild", "id", "name", "motd", "creator_db_guid", "creation_time");
            AssertHasColumns(connection, "guild_member", "player_db_guid", "guild_id", "membership");
            Assert.False(HasColumn(connection, "account", "is_archived"));
            Assert.False(HasColumn(connection, "account", "is_password_expired"));
            Assert.False(HasColumn(connection, "player", "start_target_region_override"));
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_VersionZeroDatabase_EnforcesCaseInsensitiveAccountUniqueness()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);
            PostgreSQLSchemaManager.EnsureCurrentSchema(connection);

            PostgresException emailException = Assert.Throws<PostgresException>(() => ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (2, 'LEGACY@example.com', 'DifferentPlayer', '\x01', '\x02', 0, 0)"));
            PostgresException playerNameException = Assert.Throws<PostgresException>(() => ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (3, 'different@example.com', 'legacyplayer', '\x01', '\x02', 0, 0)"));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, emailException.SqlState);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, playerNameException.SqlState);
        }

        [PostgreSQLFact]
        public void MigrationScripts_TransitionSchemaToExpectedVersion()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            for (int version = 0; version < 7; version++)
            {
                PostgreSQLSchemaManager.ApplyMigration(connection, version);
                Assert.Equal(version + 1, GetSchemaVersion(connection));

                if (version == 0)
                {
                    Assert.True(HasTable(connection, "controlled_entity"));
                    Assert.True(HasColumn(connection, "player", "start_target_region_override"));
                    ExecuteNonQuery(connection, "INSERT INTO player (db_guid) VALUES (1)");
                }
                else if (version == 1)
                {
                    Assert.True(HasColumn(connection, "account", "flags"));
                    Assert.False(HasColumn(connection, "account", "is_archived"));
                }
                else if (version == 2)
                {
                    Assert.True(HasColumn(connection, "player", "gazillionite_balance"));
                    Assert.Equal(-1, GetPlayerValue(connection, "gazillionite_balance"));
                }
                else if (version == 3)
                {
                    Assert.False(HasColumn(connection, "player", "start_target_region_override"));
                }
                else if (version == 4)
                {
                    Assert.True(HasTable(connection, "guild"));
                    Assert.True(HasTable(connection, "guild_member"));
                    Assert.Equal(0, GetPlayerValue(connection, "last_logout_time"));
                }
                else if (version == 5)
                {
                    Assert.False(HasColumn(connection, "player", "flags"));
                }
                else if (version == 6)
                {
                    Assert.True(HasColumn(connection, "player", "flags"));
                    Assert.Equal(0, GetPlayerValue(connection, "flags"));
                }
            }
        }

        [PostgreSQLFact]
        public void EnsureCurrentSchema_ConflictingVersionSixMigration_RollsBackSchemaAndMetadata()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            for (int version = 0; version < 6; version++)
            {
                PostgreSQLSchemaManager.ApplyMigration(connection, version);
            }

            ExecuteNonQuery(connection, "ALTER TABLE player ADD COLUMN flags bigint");
            int columnCount = GetColumnCount(connection, "player");

            Assert.Throws<PostgresException>(() => PostgreSQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(6, GetSchemaVersion(connection));
            Assert.Equal(columnCount, GetColumnCount(connection, "player"));
            Assert.True(HasColumn(connection, "player", "flags"));
        }

        private static int GetSchemaVersion(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT version FROM mhserveremu_schema WHERE id = 1", connection);
            return (int)command.ExecuteScalar();
        }

        private static int GetKnownApplicationTableCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new(@"
                SELECT COUNT(*)
                FROM unnest(ARRAY['account', 'player', 'avatar', 'team_up', 'item', 'controlled_entity', 'guild', 'guild_member']) AS table_name
                WHERE to_regclass(table_name) IS NOT NULL", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetAccountCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM account", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetAccountFlags(NpgsqlConnection connection, long id)
        {
            using NpgsqlCommand command = new("SELECT flags FROM account WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static long GetPlayerValue(NpgsqlConnection connection, string columnName)
        {
            using NpgsqlCommand command = new($"SELECT {columnName} FROM player WHERE db_guid = 1", connection);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static string GetAccountEmail(NpgsqlConnection connection, long id)
        {
            using NpgsqlCommand command = new("SELECT email FROM account WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            return (string)command.ExecuteScalar();
        }

        private static int GetMetadataRowCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM mhserveremu_schema", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataVersion(NpgsqlConnection connection, short id)
        {
            using NpgsqlCommand command = new("SELECT version FROM mhserveremu_schema WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            return (int)command.ExecuteScalar();
        }

        private static bool HasTable(NpgsqlConnection connection, string tableName)
        {
            using NpgsqlCommand command = new("SELECT to_regclass(@tableName) IS NOT NULL", connection);
            command.Parameters.AddWithValue("tableName", tableName);
            return (bool)command.ExecuteScalar();
        }

        private static bool HasColumn(NpgsqlConnection connection, string tableName, string columnName)
        {
            using NpgsqlCommand command = new(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = @tableName
                      AND column_name = @columnName)", connection);
            command.Parameters.AddWithValue("tableName", tableName);
            command.Parameters.AddWithValue("columnName", columnName);
            return (bool)command.ExecuteScalar();
        }

        private static int GetColumnCount(NpgsqlConnection connection, string tableName)
        {
            using NpgsqlCommand command = new(@"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = @tableName", connection);
            command.Parameters.AddWithValue("tableName", tableName);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void AssertHasColumns(NpgsqlConnection connection, string tableName, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                Assert.True(HasColumn(connection, tableName, columnName), $"Expected {tableName}.{columnName} to exist.");
            }
        }

        private static void InstallSchemaVersion0(NpgsqlConnection connection)
        {
            const string resourceName = "MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Scripts.SchemaVersion0.sql";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using StreamReader reader = new(stream);
            ExecuteNonQuery(connection, reader.ReadToEnd());
        }


        private static void ExecuteNonQuery(NpgsqlConnection connection, string commandText)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }
    }
}
