using System.Data;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    internal readonly record struct MySQLSchemaResult(bool Created, int Version);

    internal static class MySQLSchemaManager
    {
        private const int CurrentSchemaVersion = 7;
        private const string SchemaLockRole = "mhserveremu-player-account";

        private static readonly string[] KnownApplicationTables =
        [
            "account",
            "player",
            "avatar",
            "team_up",
            "item",
            "controlled_entity",
            "guild",
            "guild_member"
        ];

        public static MySQLSchemaResult EnsureCurrentSchema(
            MySqlConnection connection,
            Action<MySqlConnection, MySqlTransaction> initializeData = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("MySQL schema initialization requires an open connection.");

            string lockName = GetSchemaLockName(connection);
            return ExecuteWithAdvisoryLock(
                () => AcquireSchemaLock(connection, lockName),
                () => ReleaseSchemaLock(connection, lockName),
                () => DiscardConnection(connection),
                () => EnsureCurrentSchemaLocked(connection, initializeData));
        }

        internal static T ExecuteWithAdvisoryLock<T>(Func<bool> acquireLock, Func<object> releaseLock, Action discardConnection, Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(acquireLock);
            ArgumentNullException.ThrowIfNull(releaseLock);
            ArgumentNullException.ThrowIfNull(discardConnection);
            ArgumentNullException.ThrowIfNull(operation);

            bool lockAcquired = false;
            bool operationFailed = false;
            try
            {
                lockAcquired = acquireLock();
                if (lockAcquired == false)
                    throw new InvalidOperationException("MySQL schema initialization could not acquire its advisory lock.");

                return operation();
            }
            catch
            {
                operationFailed = true;
                throw;
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        object releaseResult = releaseLock();
                        if (releaseResult == null || Convert.ToInt32(releaseResult) != 1)
                            throw new InvalidOperationException("MySQL schema initialization could not release its advisory lock.");
                    }
                    catch when (operationFailed)
                    {
                        TryDiscardConnection(discardConnection);
                    }
                    catch
                    {
                        TryDiscardConnection(discardConnection);
                        throw;
                    }
                }
            }
        }

        internal static void ApplyMigration(MySqlConnection connection, int version)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("MySQL schema migration requires an open connection.");

            if (version < 0 || version >= CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(version));

            if (GetSchemaVersion(connection) != version)
                throw new InvalidOperationException("MySQL schema metadata does not match the requested migration.");

            ValidateKnownApplicationTables(connection, version);

            // MySQL DDL implicitly commits, so metadata is updated only after the script succeeds.
            using (MySqlCommand migrationCommand = new(MySQLScripts.GetMigrationScript(version), connection))
            {
                migrationCommand.ExecuteNonQuery();
            }

            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using MySqlCommand versionCommand = new(
                    "UPDATE mhserveremu_schema SET version = @nextVersion WHERE id = 1 AND version = @version", connection, transaction);
                versionCommand.Parameters.AddWithValue("@nextVersion", version + 1);
                versionCommand.Parameters.AddWithValue("@version", version);
                if (versionCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("MySQL schema metadata must contain exactly one row with id 1.");

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static MySQLSchemaResult EnsureCurrentSchemaLocked(
            MySqlConnection connection,
            Action<MySqlConnection, MySqlTransaction> initializeData)
        {
            if (HasTable(connection, "mhserveremu_schema") == false)
            {
                if (GetKnownApplicationTableCount(connection) > 0)
                {
                    throw new InvalidOperationException(
                        "MySQL schema metadata is missing while application tables exist.");
                }

                return InitializeSchema(connection, initializeData);
            }

            int schemaVersion = GetSchemaVersion(connection);
            if (schemaVersion < 0)
                throw new InvalidOperationException($"MySQL schema version {schemaVersion} is invalid.");

            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"MySQL schema version {schemaVersion} is unsupported; current version is {CurrentSchemaVersion}.");
            }

            while (schemaVersion < CurrentSchemaVersion)
            {
                ApplyMigration(connection, schemaVersion);
                schemaVersion = GetSchemaVersion(connection);
            }

            ValidateKnownApplicationTables(connection, CurrentSchemaVersion);
            return new MySQLSchemaResult(false, schemaVersion);
        }

        private static MySQLSchemaResult InitializeSchema(
            MySqlConnection connection,
            Action<MySqlConnection, MySqlTransaction> initializeData)
        {
            using (MySqlCommand command = new(MySQLScripts.GetInitializationScript(), connection))
            {
                command.ExecuteNonQuery();
            }

            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                initializeData?.Invoke(connection, transaction);

                using MySqlCommand versionCommand = new(
                    "INSERT INTO mhserveremu_schema (id, version) VALUES (1, @version)", connection, transaction);
                versionCommand.Parameters.AddWithValue("@version", CurrentSchemaVersion);
                if (versionCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("MySQL schema metadata must contain exactly one row with id 1.");

                transaction.Commit();
                return new MySQLSchemaResult(true, CurrentSchemaVersion);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static int GetSchemaVersion(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT id, version FROM mhserveremu_schema", connection);
            using MySqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false)
                throw new InvalidOperationException("MySQL schema metadata must contain exactly one row with id 1.");

            int schemaId = Convert.ToInt32(reader.GetValue(0));
            int schemaVersion = Convert.ToInt32(reader.GetValue(1));
            if (reader.Read() || schemaId != 1)
                throw new InvalidOperationException("MySQL schema metadata must contain exactly one row with id 1.");

            return schemaVersion;
        }

        private static void ValidateKnownApplicationTables(MySqlConnection connection, int version)
        {
            string[] expectedTables = version switch
            {
                0 => ["account", "player", "avatar"],
                >= 1 and <= 4 => ["account", "player", "avatar", "team_up", "item", "controlled_entity"],
                >= 5 and <= CurrentSchemaVersion => KnownApplicationTables,
                _ => throw new InvalidOperationException($"MySQL schema version {version} is invalid.")
            };

            int knownTableCount = GetKnownApplicationTableCount(connection);
            if (knownTableCount != expectedTables.Length || expectedTables.Any(tableName => HasTable(connection, tableName) == false))
            {
                throw new InvalidOperationException(
                    $"MySQL application tables are inconsistent with recorded schema version {version}.");
            }

            ValidateRecordedSchemaShape(connection, version);
        }

        private static void ValidateRecordedSchemaShape(MySqlConnection connection, int version)
        {
            ValidateColumns(connection, "account", "id", "email", "player_name", "password_hash", "salt", "user_level");
            ValidateColumn(connection, "account", "is_banned", version == 0 || version == 1);
            ValidateColumn(connection, "account", "is_archived", version == 0 || version == 1);
            ValidateColumn(connection, "account", "is_password_expired", version == 0 || version == 1);
            ValidateColumn(connection, "account", "flags", version >= 2);
            ValidateUniqueIndex(connection, "account", "ux_account_email_ci", version >= 1, "email");
            ValidateUniqueIndex(connection, "account", "ux_account_player_name_ci", version >= 1, "player_name");

            if (version == 0)
            {
                ValidateColumns(connection, "player", "id");
                ValidateColumns(connection, "avatar", "id");
                ValidateColumn(connection, "player", "db_guid", false);
                ValidateColumn(connection, "avatar", "db_guid", false);
                return;
            }

            ValidateColumn(connection, "player", "id", false);
            ValidateColumns(connection, "player", "db_guid", "archive_data", "start_target", "aoi_volume");
            ValidateColumn(connection, "player", "start_target_region_override", version <= 3);
            ValidateColumn(connection, "player", "gazillionite_balance", version >= 3);
            ValidateColumn(connection, "player", "last_logout_time", version >= 5);
            ValidateColumn(connection, "player", "flags", version >= 7);

            ValidateContainerTable(connection, "avatar");
            ValidateContainerTable(connection, "team_up");
            ValidateContainerTable(connection, "item");
            ValidateContainerTable(connection, "controlled_entity");

            if (version >= 5)
            {
                ValidateColumns(connection, "guild", "id", "name", "motd", "creator_db_guid", "creation_time");
                ValidateUniqueIndex(connection, "guild", "ux_guild_name_ci", true, "name");
                ValidateColumns(connection, "guild_member", "player_db_guid", "guild_id", "membership");
            }
        }

        private static void ValidateContainerTable(MySqlConnection connection, string tableName)
        {
            ValidateColumns(connection, tableName, "db_guid", "container_db_guid", "inventory_proto_guid", "slot", "entity_proto_guid", "archive_data");
            ValidateIndex(connection, tableName, $"ix_{tableName}_container_db_guid", true);
        }

        private static void ValidateColumns(MySqlConnection connection, string tableName, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                ValidateColumn(connection, tableName, columnName, true);
            }
        }

        private static void ValidateColumn(MySqlConnection connection, string tableName, string columnName, bool expected)
        {
            if (HasColumn(connection, tableName, columnName) != expected)
                throw new InvalidOperationException("MySQL application tables are inconsistent with recorded schema version.");
        }

        private static void ValidateIndex(MySqlConnection connection, string tableName, string indexName, bool expected)
        {
            if (HasIndex(connection, tableName, indexName) != expected)
                throw new InvalidOperationException("MySQL application tables are inconsistent with recorded schema version.");
        }

        private static void ValidateUniqueIndex(
            MySqlConnection connection,
            string tableName,
            string indexName,
            bool expected,
            params string[] columnNames)
        {
            using MySqlCommand command = new(@"
                SELECT statistics.non_unique, statistics.seq_in_index, statistics.column_name, columns.collation_name
                FROM information_schema.statistics AS statistics
                LEFT JOIN information_schema.columns AS columns
                    ON columns.table_schema = statistics.table_schema
                    AND columns.table_name = statistics.table_name
                    AND columns.column_name = statistics.column_name
                WHERE statistics.table_schema = DATABASE()
                  AND statistics.table_name = @tableName
                  AND statistics.index_name = @indexName
                ORDER BY statistics.seq_in_index", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            command.Parameters.AddWithValue("@indexName", indexName);
            using MySqlDataReader reader = command.ExecuteReader();

            if (expected == false)
            {
                if (reader.Read())
                    throw new InvalidOperationException("MySQL application tables are inconsistent with recorded schema version.");

                return;
            }

            for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
            {
                if (reader.Read() == false
                    || Convert.ToBoolean(reader.GetValue(0))
                    || Convert.ToInt32(reader.GetValue(1)) != columnIndex + 1
                    || string.Equals(Convert.ToString(reader.GetValue(2)), columnNames[columnIndex], StringComparison.Ordinal) == false
                    || Convert.ToString(reader.GetValue(3)).EndsWith("_ci", StringComparison.OrdinalIgnoreCase) == false)
                {
                    throw new InvalidOperationException("MySQL application tables are inconsistent with recorded schema version.");
                }
            }

            if (reader.Read())
                throw new InvalidOperationException("MySQL application tables are inconsistent with recorded schema version.");
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

        private static bool AcquireSchemaLock(MySqlConnection connection, string lockName)
        {
            using MySqlCommand command = new("SELECT GET_LOCK(@lockName, 30)", connection);
            command.Parameters.AddWithValue("@lockName", lockName);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        private static object ReleaseSchemaLock(MySqlConnection connection, string lockName)
        {
            using MySqlCommand command = new("SELECT RELEASE_LOCK(@lockName)", connection);
            command.Parameters.AddWithValue("@lockName", lockName);
            return command.ExecuteScalar();
        }

        private static void DiscardConnection(MySqlConnection connection)
        {
            try
            {
                MySqlConnection.ClearPool(connection);
            }
            finally
            {
                connection.Dispose();
            }
        }

        private static void TryDiscardConnection(Action discardConnection)
        {
            try
            {
                discardConnection();
            }
            catch
            {
            }
        }

        private static string GetSchemaLockName(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT DATABASE()", connection);
            string databaseName = Convert.ToString(command.ExecuteScalar());
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new InvalidOperationException("MySQL schema initialization requires a selected database.");

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SchemaLockRole}:{databaseName}"));
            return $"{SchemaLockRole}:{Convert.ToHexString(hash)[..32]}";
        }
    }
}
