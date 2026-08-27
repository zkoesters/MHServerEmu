using System.Security.Cryptography;
using System.Text;
using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLLeaderboardSchemaManagerTests
    {
        [Fact]
        public void EnsureCurrentSchema_ClosedConnection_RequiresOpenConnection()
        {
            using MySqlConnection connection = new();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("open connection", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("NO ACTION", true)]
        [InlineData("RESTRICT", true)]
        [InlineData("CASCADE", false)]
        [InlineData("SET NULL", false)]
        [InlineData(null, false)]
        public void IsExpectedForeignKeyUpdateRule_OnlyAllowsNoActionOrRestrict(string updateRule, bool expected)
        {
            Assert.Equal(expected, MySQLLeaderboardSchemaManager.IsExpectedForeignKeyUpdateRule(updateRule));
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_AcquisitionFails_DoesNotReleaseOrDiscardConnection()
        {
            int releaseCount = 0;
            int discardCount = 0;

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.ExecuteWithAdvisoryLock(
                () => false,
                () => releaseCount++,
                () => discardCount++,
                () => 0));

            Assert.Equal(0, releaseCount);
            Assert.Equal(0, discardCount);
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_ReleaseThrowsAfterOperationFailure_DiscardsConnectionAndPreservesOperationFailure()
        {
            InvalidOperationException operationFailure = new("operation failure");
            bool connectionDiscarded = false;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.ExecuteWithAdvisoryLock<int>(
                () => true,
                () => throw new InvalidOperationException("release failure"),
                () => connectionDiscarded = true,
                () => throw operationFailure));

            Assert.Same(operationFailure, exception);
            Assert.True(connectionDiscarded);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public void ExecuteWithAdvisoryLock_InvalidReleaseResultAfterOperationFailure_DiscardsConnectionAndPreservesOperationFailure(object releaseResult)
        {
            InvalidOperationException operationFailure = new("operation failure");
            bool connectionDiscarded = false;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.ExecuteWithAdvisoryLock<int>(
                () => true,
                () => releaseResult,
                () => connectionDiscarded = true,
                () => throw operationFailure));

            Assert.Same(operationFailure, exception);
            Assert.True(connectionDiscarded);
        }

        [Fact]
        public void ExecuteWithAdvisoryLock_ReleaseThrowsAfterOperationSuccess_DiscardsConnectionAndSurfacesReleaseFailure()
        {
            bool connectionDiscarded = false;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.ExecuteWithAdvisoryLock(
                () => true,
                () => throw new InvalidOperationException("release failure"),
                () => connectionDiscarded = true,
                () => 0));

            Assert.Equal("release failure", exception.Message);
            Assert.True(connectionDiscarded);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public void ExecuteWithAdvisoryLock_InvalidReleaseResultAfterOperationSuccess_DiscardsConnectionAndSurfacesReleaseFailure(object releaseResult)
        {
            bool connectionDiscarded = false;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.ExecuteWithAdvisoryLock(
                () => true,
                () => releaseResult,
                () => connectionDiscarded = true,
                () => 0));

            Assert.Contains("release", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(connectionDiscarded);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_FreshDatabase_InitializesVersionOneSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();

            MySQLLeaderboardSchemaResult result = MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            Assert.True(result.Created);
            Assert.Equal(1, result.Version);
            Assert.Equal(1, GetSchemaVersion(connection));
            Assert.Equal(1, GetMetadataRowCount(connection));
            Assert.Equal(5, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabase_ReturnsExistingSchema()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            MySQLLeaderboardSchemaResult result = MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            Assert.False(result.Created);
            Assert.Equal(1, result.Version);
        }

        [MySQLFact]
        public void EnsureCurrentSchema_EmptyMetadata_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            CreateMetadataTable(connection);

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(0, GetMetadataRowCount(connection));
            Assert.Equal(0, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_MetadataWithWrongId_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            CreateMetadataTable(connection);
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (2, 1)");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(1, GetMetadataRowCount(connection));
            Assert.Equal(1, GetMetadataVersion(connection, 2));
            Assert.Equal(0, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_DuplicateMetadata_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            CreateMetadataTable(connection);
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (1, 1), (2, 1)");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(2, GetMetadataRowCount(connection));
            Assert.Equal(0, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_OlderMetadataVersion_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            CreateMetadataTable(connection);
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (1, 0)");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(0, GetSchemaVersion(connection));
            Assert.Equal(0, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_NewerMetadataVersion_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            CreateMetadataTable(connection);
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (1, 2)");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(2, GetSchemaVersion(connection));
            Assert.Equal(0, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_LeaderboardTableWithoutMetadata_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE leaderboard (leaderboard_id BIGINT PRIMARY KEY) ENGINE=InnoDB");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.True(HasTable(connection, "leaderboard"));
            Assert.False(HasTable(connection, "mhserveremu_leaderboards_schema"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_PartialDdlFailureLeavesUnversionedSchemaThatRejectsLaterStartup()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();

            Assert.Throws<MySqlException>(() => ExecuteNonQuery(connection, @"
                CREATE TABLE mhserveremu_leaderboards_schema (
                    id SMALLINT PRIMARY KEY CHECK (id = 1),
                    version INT NOT NULL
                ) ENGINE=InnoDB;
                CREATE TABLE leaderboard (leaderboard_id BIGINT PRIMARY KEY) ENGINE=InnoDB;
                INVALID SQL"));

            Assert.True(HasTable(connection, "mhserveremu_leaderboards_schema"));
            Assert.True(HasTable(connection, "leaderboard"));
            Assert.Equal(0, GetMetadataRowCount(connection));
            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));
            Assert.True(HasTable(connection, "leaderboard"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_DdlOnlySchemaWithoutMetadataVersion_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, MySQLScripts.GetLeaderboardInitializationScript());

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(0, GetMetadataRowCount(connection));
            Assert.Equal(5, GetLeaderboardTableCount(connection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_MissingRequiredLeaderboardTable_RejectsWithoutRepair()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, "DROP TABLE leaderboard_reward");

            Assert.Throws<InvalidOperationException>(() => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Equal(1, GetSchemaVersion(connection));
            Assert.False(HasTable(connection, "leaderboard_reward"));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithMissingColumn_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE leaderboard DROP COLUMN prototype_name");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithWrongColumnDefinition_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE leaderboard MODIFY max_reset_count BIGINT");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithMissingPrimaryKey_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE leaderboard_reward DROP PRIMARY KEY");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithMissingForeignKey_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE leaderboard_reward DROP FOREIGN KEY fk_leaderboard_reward_instance");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithMissingNamedIndex_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE leaderboard_reward DROP INDEX idx_rewards_participantid");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithUnwantedMetadataInstanceForeignKey_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation(@"
                ALTER TABLE leaderboard_meta_entry
                ADD CONSTRAINT fk_leaderboard_meta_entry_instance
                FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithMyIsamTable_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation("ALTER TABLE mhserveremu_leaderboards_schema ENGINE=MyISAM");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithCaseSensitivePrototypeNameCollation_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation(
                "ALTER TABLE leaderboard MODIFY prototype_name VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_CurrentDatabaseWithUnexpectedCheckConstraint_RejectsSchema()
        {
            AssertCurrentSchemaRejectsMutation(
                "ALTER TABLE leaderboard_entry ADD CONSTRAINT chk_leaderboard_entry_score CHECK (score >= 0)");
        }

        [MySQLFact]
        public void EnsureCurrentSchema_AccountSchemaState_DoesNotPreventLeaderboardInitialization()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "CREATE TABLE mhserveremu_schema (id SMALLINT PRIMARY KEY, version INT NOT NULL) ENGINE=InnoDB");
            ExecuteNonQuery(connection, "INSERT INTO mhserveremu_schema (id, version) VALUES (1, 7)");
            ExecuteNonQuery(connection, "CREATE TABLE account (id BIGINT PRIMARY KEY) ENGINE=InnoDB");

            MySQLLeaderboardSchemaResult result = MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);

            Assert.True(result.Created);
            Assert.Equal(1, result.Version);
            Assert.True(HasTable(connection, "account"));
            Assert.Equal(7, GetVersion(connection, "mhserveremu_schema"));
            Assert.Equal(1, GetSchemaVersion(connection));
        }

        [MySQLFact]
        public async Task EnsureCurrentSchema_ConcurrentInitialization_CreatesExactlyOneSchema()
        {
            using MySQLTestDatabase database = new();
            const int workerCount = 4;
            using Barrier barrier = new(workerCount);
            Task<MySQLLeaderboardSchemaResult>[] workers = Enumerable.Range(0, workerCount)
                .Select(_ => Task.Run(() =>
                {
                    using MySqlConnection connection = database.OpenConnection();
                    barrier.SignalAndWait();
                    return MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
                }))
                .ToArray();
            MySQLLeaderboardSchemaResult[] results = await Task.WhenAll(workers);

            Assert.Single(results, result => result.Created);
            Assert.All(results, result => Assert.Equal(1, result.Version));
            using MySqlConnection assertionConnection = database.OpenConnection();
            Assert.Equal(1, GetSchemaVersion(assertionConnection));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_ReleasesDatabaseScopedNamedLock()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection initializationConnection = database.OpenConnection();
            using MySqlConnection lockConnection = database.OpenConnection();
            MySQLLeaderboardSchemaManager.EnsureCurrentSchema(initializationConnection);

            string lockName = GetLockName("mhserveremu-leaderboards", database.DatabaseName);
            Assert.Equal(1, ExecuteScalar<int>(lockConnection, "SELECT GET_LOCK(@lockName, 0)", ("@lockName", lockName)));
            Assert.Equal(1, ExecuteScalar<int>(lockConnection, "SELECT RELEASE_LOCK(@lockName)", ("@lockName", lockName)));
        }

        [MySQLFact]
        public void EnsureCurrentSchema_HeldAccountSchemaLock_DoesNotBlockLeaderboardInitialization()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection accountLockConnection = database.OpenConnection();
            using MySqlConnection leaderboardConnection = database.OpenConnection();
            string accountLockName = GetLockName("mhserveremu-player-account", database.DatabaseName);
            string leaderboardLockName = GetLockName("mhserveremu-leaderboards", database.DatabaseName);

            Assert.NotEqual(accountLockName, leaderboardLockName);
            Assert.Equal(1, ExecuteScalar<int>(accountLockConnection, "SELECT GET_LOCK(@lockName, 0)", ("@lockName", accountLockName)));
            try
            {
                MySQLLeaderboardSchemaResult result = MySQLLeaderboardSchemaManager.EnsureCurrentSchema(leaderboardConnection);

                Assert.True(result.Created);
                Assert.Equal(1, result.Version);
            }
            finally
            {
                TryReleaseLock(accountLockConnection, accountLockName);
            }
        }

        private static void CreateMetadataTable(MySqlConnection connection)
        {
            ExecuteNonQuery(connection, "CREATE TABLE mhserveremu_leaderboards_schema (id SMALLINT, version INT NOT NULL) ENGINE=InnoDB");
        }

        private static void AssertCurrentSchemaRejectsMutation(string mutation)
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
            ExecuteNonQuery(connection, mutation);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection));

            Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetSchemaVersion(MySqlConnection connection)
        {
            return GetVersion(connection, "mhserveremu_leaderboards_schema");
        }

        private static int GetVersion(MySqlConnection connection, string tableName)
        {
            using MySqlCommand command = new($"SELECT version FROM {tableName} WHERE id = 1", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataVersion(MySqlConnection connection, short id)
        {
            using MySqlCommand command = new("SELECT version FROM mhserveremu_leaderboards_schema WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetMetadataRowCount(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT COUNT(*) FROM mhserveremu_leaderboards_schema", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetLeaderboardTableCount(MySqlConnection connection)
        {
            using MySqlCommand command = new(@"
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name IN ('leaderboard', 'leaderboard_instance', 'leaderboard_entry', 'leaderboard_meta_entry', 'leaderboard_reward')", connection);
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

        private static T ExecuteScalar<T>(MySqlConnection connection, string commandText, params (string Name, object Value)[] parameters)
        {
            using MySqlCommand command = new(commandText, connection);
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return (T)Convert.ChangeType(command.ExecuteScalar(), typeof(T));
        }

        private static void ExecuteNonQuery(MySqlConnection connection, string commandText)
        {
            using MySqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }

        private static void TryReleaseLock(MySqlConnection connection, string lockName)
        {
            try
            {
                ExecuteScalar<int>(connection, "SELECT RELEASE_LOCK(@lockName)", ("@lockName", lockName));
            }
            catch
            {
            }
        }

        private static string GetLockName(string role, string databaseName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{role}:{databaseName}"));
            return $"{role}:{Convert.ToHexString(hash)[..32]}";
        }
    }
}
