using System.Reflection;
using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLSchemaManagerTests
    {
        [Fact]
        public void EnsureCurrentSchema_ClosedConnection_RequiresOpenConnection()
        {
            using MySqlConnection connection = new();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("open connection", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InitializeDatabaseScript_DoesNotInsertSchemaMetadata()
        {
            Assert.DoesNotContain("INSERT INTO mhserveremu_schema", MySQLScripts.GetInitializationScript(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_AcquisitionFails_DoesNotReleaseLock()
        {
            int releaseCount = 0;
            int discardCount = 0;

            Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.ExecuteWithAdvisoryLock(
                () => false,
                () => releaseCount++,
                () => discardCount++,
                () => 0));

            Assert.Equal(0, releaseCount);
            Assert.Equal(0, discardCount);
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_ReleaseFailsAfterOperationFailure_PreservesOperationFailure()
        {
            InvalidOperationException operationFailure = new("operation failure");
            int releaseCount = 0;
            int discardCount = 0;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.ExecuteWithAdvisoryLock<int>(
                () => true,
                () =>
                {
                    releaseCount++;
                    throw new InvalidOperationException("release failure");
                },
                () => discardCount++,
                () => throw operationFailure));

            Assert.Same(operationFailure, exception);
            Assert.Equal(1, releaseCount);
            Assert.Equal(1, discardCount);
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_ReleaseFailsAfterOperationSuccess_SurfacesReleaseFailure()
        {
            int discardCount = 0;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.ExecuteWithAdvisoryLock(
                () => true,
                () => throw new InvalidOperationException("release failure"),
                () => discardCount++,
                () => 0));

            Assert.Equal("release failure", exception.Message);
            Assert.Equal(1, discardCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(null)]
        public void ExecuteWithAdvisoryLock_InvalidReleaseResultAfterOperationSuccess_DiscardsConnection(object releaseResult)
        {
            int discardCount = 0;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.ExecuteWithAdvisoryLock(
                () => true,
                () => releaseResult,
                () => discardCount++,
                () => 0));

            Assert.Contains("release", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, discardCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(null)]
        public void ExecuteWithAdvisoryLock_InvalidReleaseResultAfterOperationFailure_PreservesOperationFailure(object releaseResult)
        {
            InvalidOperationException operationFailure = new("operation failure");
            int discardCount = 0;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.ExecuteWithAdvisoryLock<int>(
                () => true,
                () => releaseResult,
                () => discardCount++,
                () => throw operationFailure));

            Assert.Same(operationFailure, exception);
            Assert.Equal(1, discardCount);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_FreshDatabase_InitializesCurrentSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();

            MySQLSchemaResult result = MySQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.True(result.Created);
            Assert.Equal(7, result.Version);
            Assert.Equal(7, GetSchemaVersion(connection));
            Assert.Equal(8, GetKnownApplicationTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabase_ReturnsExistingSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLSchemaManager.EnsureCurrentSchema(connection);

            MySQLSchemaResult result = MySQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.False(result.Created);
            Assert.Equal(7, result.Version);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithNonUniqueNamedUniqueIndex_RejectsSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE account DROP INDEX ux_account_email_ci");
            ExecuteNonQuery(connection, "CREATE INDEX ux_account_email_ci ON account (email)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithWrongNamedUniqueIndexColumns_RejectsSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE account DROP INDEX ux_account_email_ci");
            ExecuteNonQuery(connection, "CREATE UNIQUE INDEX ux_account_email_ci ON account (player_name, email)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithCaseSensitiveNamedUniqueIndex_RejectsSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "ALTER TABLE account DROP INDEX ux_account_email_ci");
            ExecuteNonQuery(connection, "ALTER TABLE account MODIFY email VARCHAR(320) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL");
            ExecuteNonQuery(connection, "CREATE UNIQUE INDEX ux_account_email_ci ON account (email)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_NewerMetadataVersion_RejectsWithoutChangingVersion()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "UPDATE mhserveremu_schema SET version = 8 WHERE id = 1");
            ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (42, 'schema-marker@example.com', 'SchemaMarker', X'01', X'02', 0, 0)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(8, GetSchemaVersion(connection));
            Assert.Equal(1, GetAccountCount(connection));
            Assert.Equal("schema-marker@example.com", GetAccountEmail(connection, 42));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_MetadataWithExtraRow_RejectsWithoutChangingMetadata()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE mhserveremu_schema (id SMALLINT, version INT NOT NULL) ENGINE=InnoDB");
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_schema (id, version) VALUES (1, 7), (2, 7)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, GetMetadataRowCount(connection));
            Assert.Equal(7, GetSchemaVersion(connection));
            Assert.Equal(7, GetMetadataVersion(connection, 2));
            Assert.Equal(0, GetKnownApplicationTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_MetadataWithoutIdOneRow_RejectsWithoutChangingMetadata()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE mhserveremu_schema (id SMALLINT, version INT NOT NULL) ENGINE=InnoDB");
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_schema (id, version) VALUES (2, 7)");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, GetMetadataRowCount(connection));
            Assert.Equal(7, GetMetadataVersion(connection, 2));
            Assert.Equal(0, GetKnownApplicationTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_ApplicationTableWithoutMetadata_RejectsWithoutDroppingTable()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE account (id BIGINT PRIMARY KEY) ENGINE=InnoDB");

            Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.True(HasTable(connection, "account"));
            Assert.False(HasTable(connection, "mhserveremu_schema"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_KnownTablesInconsistentWithRecordedVersion_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);
            ExecuteNonQuery(connection, "DROP TABLE player");
            ExecuteNonQuery(connection, "CREATE TABLE team_up (id BIGINT PRIMARY KEY) ENGINE=InnoDB");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, GetSchemaVersion(connection));
            Assert.False(HasTable(connection, "player"));
            Assert.True(HasTable(connection, "team_up"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_PartialMigrationZeroIndexes_RejectsWithoutRetryingDdl()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);
            ExecuteNonQuery(connection, @"
                INSERT INTO account (
                    id, email, player_name, password_hash, salt, user_level, is_banned, is_archived, is_password_expired
                )
                VALUES (2, 'second@example.com', 'LegacyPlayer', X'01', X'02', 0, 0, 0, 0)");

            Assert.Throws<MySqlException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(0, GetSchemaVersion(connection));
            Assert.True(HasIndex(connection, "account", "ux_account_email_ci"));
            Assert.False(HasIndex(connection, "account", "ux_account_player_name_ci"));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, GetSchemaVersion(connection));
            Assert.True(HasIndex(connection, "account", "ux_account_email_ci"));
            Assert.False(HasIndex(connection, "account", "ux_account_player_name_ci"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_InitializationCallbackFails_LeavesUnversionedSchemaAndRejectsNextStartup()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();

            Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(
                connection,
                (_, _) => throw new InvalidOperationException("Initialization callback failed.")));

            Assert.True(HasTable(connection, "mhserveremu_schema"));
            Assert.Equal(0, GetMetadataRowCount(connection));
            Assert.Equal(8, GetKnownApplicationTableCount(connection));
            Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_VersionZeroDatabase_MigratesToCurrentSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            MySQLSchemaResult result = MySQLSchemaManager.EnsureCurrentSchema(connection);

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

        [MySQLFact]
        public void EnsureCurrentSchema_VersionZeroDatabase_EnforcesCaseInsensitiveUniqueness()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);
            MySQLSchemaManager.EnsureCurrentSchema(connection);

            Assert.Throws<MySqlException>(() => ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (2, 'LEGACY@example.com', 'DifferentPlayer', X'01', X'02', 0, 0)"));
            Assert.Throws<MySqlException>(() => ExecuteNonQuery(connection, @"
                INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (3, 'different@example.com', 'legacyplayer', X'01', X'02', 0, 0)"));
            ExecuteNonQuery(connection, @"
                INSERT INTO guild (id, name, motd) VALUES (1, 'GuildName', 'motd')");
            Assert.Throws<MySqlException>(() => ExecuteNonQuery(connection, @"
                INSERT INTO guild (id, name, motd) VALUES (2, 'guildname', 'motd')"));
        }

        [MySQLFact]
        public void MigrationScripts_TransitionSchemaToExpectedVersion()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            for (int version = 0; version < 7; version++)
            {
                MySQLSchemaManager.ApplyMigration(connection, version);
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

        [MySQLFact]
        public void EnsureCurrentSchema_ConflictingVersionSixMigration_LeavesMetadataUnchanged()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            InstallSchemaVersion0(connection);

            for (int version = 0; version < 6; version++)
            {
                MySQLSchemaManager.ApplyMigration(connection, version);
            }

            ExecuteNonQuery(connection, "ALTER TABLE player ADD COLUMN flags BIGINT");
            int columnCount = GetColumnCount(connection, "player");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(6, GetSchemaVersion(connection));
            Assert.Equal(columnCount, GetColumnCount(connection, "player"));
            Assert.True(HasColumn(connection, "player", "flags"));
        }

        [MySQLFact]
        public async Task EnsureCurrentSchema_ConcurrentInitialization_CreatesOneCurrentSchema()
        {
            using MySQLTestDatabase database = new();
            await using MySqlConnection firstConnection = database.OpenConnection();
            await using MySqlConnection secondConnection = database.OpenConnection();

            Task<MySQLSchemaResult> first = Task.Run(() => MySQLSchemaManager.EnsureCurrentSchema(firstConnection));
            Task<MySQLSchemaResult> second = Task.Run(() => MySQLSchemaManager.EnsureCurrentSchema(secondConnection));
            MySQLSchemaResult[] results = await Task.WhenAll(first, second);

            Assert.Single(results, result => result.Created);
            Assert.All(results, result => Assert.Equal(7, result.Version));
            Assert.Equal(7, GetSchemaVersion(firstConnection));
        }

        private static int GetSchemaVersion(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT version FROM mhserveremu_schema WHERE id = 1", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetKnownApplicationTableCount(MySqlConnection connection)
        {
            using MySqlCommand command = new(@"
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name IN ('account', 'player', 'avatar', 'team_up', 'item', 'controlled_entity', 'guild', 'guild_member')", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetAccountCount(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT COUNT(*) FROM account", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetAccountFlags(MySqlConnection connection, long id)
        {
            using MySqlCommand command = new("SELECT flags FROM account WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static long GetPlayerValue(MySqlConnection connection, string columnName)
        {
            using MySqlCommand command = new($"SELECT {columnName} FROM player WHERE db_guid = 1", connection);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static string GetAccountEmail(MySqlConnection connection, long id)
        {
            using MySqlCommand command = new("SELECT email FROM account WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return (string)command.ExecuteScalar();
        }

        private static int GetMetadataRowCount(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT COUNT(*) FROM mhserveremu_schema", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataVersion(MySqlConnection connection, short id)
        {
            using MySqlCommand command = new("SELECT version FROM mhserveremu_schema WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static bool HasTable(MySqlConnection connection, string tableName)
        {
            using MySqlCommand command = new(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = DATABASE()
                      AND table_name = @tableName)", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        private static bool HasColumn(MySqlConnection connection, string tableName, string columnName)
        {
            using MySqlCommand command = new(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = DATABASE()
                      AND table_name = @tableName
                      AND column_name = @columnName)", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            command.Parameters.AddWithValue("@columnName", columnName);
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        private static bool HasIndex(MySqlConnection connection, string tableName, string indexName)
        {
            using MySqlCommand command = new(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.statistics
                    WHERE table_schema = DATABASE()
                      AND table_name = @tableName
                      AND index_name = @indexName)", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            command.Parameters.AddWithValue("@indexName", indexName);
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        private static int GetColumnCount(MySqlConnection connection, string tableName)
        {
            using MySqlCommand command = new(@"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                  AND table_name = @tableName", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void AssertHasColumns(MySqlConnection connection, string tableName, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                Assert.True(HasColumn(connection, tableName, columnName), $"Expected {tableName}.{columnName} to exist.");
            }
        }

        private static void InstallSchemaVersion0(MySqlConnection connection)
        {
            const string resourceName = "MHServerEmu.DatabaseAccess.Tests.MySQL.Scripts.SchemaVersion0.sql";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using StreamReader reader = new(stream);
            ExecuteNonQuery(connection, reader.ReadToEnd());
        }

        private static void ExecuteNonQuery(MySqlConnection connection, string commandText)
        {
            using MySqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }
    }
}
