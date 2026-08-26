using System.Data;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    internal readonly record struct MySQLLeaderboardSchemaResult(bool Created, int Version);

    internal static class MySQLLeaderboardSchemaManager
    {
        private const int CurrentSchemaVersion = 1;
        private const string SchemaLockRole = "mhserveremu-leaderboards";

        private static readonly string[] KnownApplicationTables =
        [
            "leaderboard",
            "leaderboard_instance",
            "leaderboard_entry",
            "leaderboard_meta_entry",
            "leaderboard_reward"
        ];

        private readonly record struct ColumnDefinition(
            string Name,
            string DataType,
            string ColumnType,
            bool Nullable,
            long? CharacterMaximumLength = null,
            string CharacterSetName = null,
            string CollationName = null);
        private readonly record struct IndexDefinition(string Name, bool Unique, string[] Columns);
        private readonly record struct ForeignKeyDefinition(string Name, string TableName, string ColumnName, string ReferencedTableName, string ReferencedColumnName);

        public static MySQLLeaderboardSchemaResult EnsureCurrentSchema(MySqlConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("MySQL leaderboard schema initialization requires an open connection.");

            string lockName = GetSchemaLockName(connection);
            return ExecuteWithAdvisoryLock(
                () => AcquireSchemaLock(connection, lockName),
                () => ReleaseSchemaLock(connection, lockName),
                () => DiscardConnection(connection),
                () => EnsureCurrentSchemaLocked(connection));
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
                    throw new InvalidOperationException("MySQL leaderboard schema initialization could not acquire its advisory lock.");

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
                            throw new InvalidOperationException("MySQL leaderboard schema initialization could not release its advisory lock.");
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

        private static MySQLLeaderboardSchemaResult EnsureCurrentSchemaLocked(MySqlConnection connection)
        {
            if (HasTable(connection, "mhserveremu_leaderboards_schema") == false)
            {
                if (GetKnownApplicationTableCount(connection) > 0)
                {
                    throw new InvalidOperationException(
                        "MySQL leaderboard schema metadata is missing while leaderboard tables exist.");
                }

                return InitializeSchema(connection);
            }

            int schemaVersion = GetSchemaVersion(connection);
            if (schemaVersion < CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"MySQL leaderboard schema version {schemaVersion} is invalid; current version is {CurrentSchemaVersion}.");
            }

            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"MySQL leaderboard schema version {schemaVersion} is unsupported; current version is {CurrentSchemaVersion}.");
            }

            ValidateKnownApplicationTables(connection);
            return new MySQLLeaderboardSchemaResult(false, schemaVersion);
        }

        private static MySQLLeaderboardSchemaResult InitializeSchema(MySqlConnection connection)
        {
            // MySQL DDL implicitly commits; leave any failed initialization unversioned for explicit operator repair.
            using (MySqlCommand command = new(MySQLScripts.GetLeaderboardInitializationScript(), connection))
            {
                command.ExecuteNonQuery();
            }

            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using MySqlCommand versionCommand = new(
                    "INSERT INTO mhserveremu_leaderboards_schema (id, version) VALUES (1, @version)", connection, transaction);
                versionCommand.Parameters.AddWithValue("@version", CurrentSchemaVersion);
                if (versionCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("MySQL leaderboard schema metadata must contain exactly one row with id 1.");

                transaction.Commit();
                return new MySQLLeaderboardSchemaResult(true, CurrentSchemaVersion);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static int GetSchemaVersion(MySqlConnection connection)
        {
            using MySqlCommand command = new("SELECT id, version FROM mhserveremu_leaderboards_schema", connection);
            using MySqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false)
                throw new InvalidOperationException("MySQL leaderboard schema metadata must contain exactly one row with id 1.");

            int schemaId = Convert.ToInt32(reader.GetValue(0));
            int schemaVersion = Convert.ToInt32(reader.GetValue(1));
            if (reader.Read() || schemaId != 1)
                throw new InvalidOperationException("MySQL leaderboard schema metadata must contain exactly one row with id 1.");

            return schemaVersion;
        }

        private static void ValidateKnownApplicationTables(MySqlConnection connection)
        {
            int knownTableCount = GetKnownApplicationTableCount(connection);
            if (knownTableCount != KnownApplicationTables.Length || KnownApplicationTables.Any(tableName => HasTable(connection, tableName) == false))
            {
                throw new InvalidOperationException(
                    "MySQL leaderboard tables are inconsistent with recorded schema version.");
            }

            ValidateMetadataTable(connection);
            ValidateCheckConstraints(connection);
            ValidateTableStorage(connection, "mhserveremu_leaderboards_schema");
            foreach (string tableName in KnownApplicationTables)
                ValidateTableStorage(connection, tableName);

            ValidateTableColumns(connection, "leaderboard",
                new("leaderboard_id", "bigint", "bigint", false),
                new("prototype_name", "varchar", "varchar(255)", true, 255, "utf8mb4", "utf8mb4_unicode_ci"),
                new("active_instance_id", "bigint", "bigint", true),
                new("is_enabled", "tinyint", "tinyint(1)", true),
                new("start_time", "bigint", "bigint", true),
                new("max_reset_count", "int", "int", true));
            ValidateTableColumns(connection, "leaderboard_instance",
                new("instance_id", "bigint", "bigint", false),
                new("leaderboard_id", "bigint", "bigint", false),
                new("state", "int", "int", true),
                new("activation_date", "bigint", "bigint", true),
                new("visible", "tinyint", "tinyint(1)", true));
            ValidateTableColumns(connection, "leaderboard_entry",
                new("instance_id", "bigint", "bigint", false),
                new("participant_id", "bigint", "bigint", false),
                new("score", "bigint", "bigint", true),
                new("high_score", "bigint", "bigint", true),
                new("rule_states", "longblob", "longblob", true, 4294967295));
            ValidateTableColumns(connection, "leaderboard_meta_entry",
                new("leaderboard_id", "bigint", "bigint", false),
                new("instance_id", "bigint", "bigint", false),
                new("sub_leaderboard_id", "bigint", "bigint", false),
                new("sub_instance_id", "bigint", "bigint", false));
            ValidateTableColumns(connection, "leaderboard_reward",
                new("leaderboard_id", "bigint", "bigint", false),
                new("instance_id", "bigint", "bigint", false),
                new("participant_id", "bigint", "bigint", false),
                new("rank", "int", "int", false),
                new("reward_id", "bigint", "bigint", false),
                new("creation_date", "bigint", "bigint", true),
                new("rewarded_date", "bigint", "bigint", true));

            ValidateIndexes(connection, "mhserveremu_leaderboards_schema",
                new IndexDefinition("PRIMARY", true, ["id"]));
            ValidateIndexes(connection, "leaderboard",
                new IndexDefinition("PRIMARY", true, ["leaderboard_id"]));
            ValidateIndexes(connection, "leaderboard_instance",
                new("PRIMARY", true, ["instance_id"]),
                new("idx_instances_leaderboardid", false, ["leaderboard_id"]));
            ValidateIndexes(connection, "leaderboard_entry",
                new("PRIMARY", true, ["instance_id", "participant_id"]),
                new("idx_entries_instanceid", false, ["instance_id"]));
            ValidateIndexes(connection, "leaderboard_meta_entry",
                new("PRIMARY", true, ["leaderboard_id", "instance_id", "sub_leaderboard_id"]),
                new("idx_meta_entries_leaderboardid", false, ["leaderboard_id"]));
            ValidateIndexes(connection, "leaderboard_reward",
                new("PRIMARY", true, ["leaderboard_id", "instance_id", "participant_id"]),
                new("idx_rewards_instanceid", false, ["instance_id"]),
                new("idx_rewards_participantid", false, ["participant_id"]));

            ValidateForeignKeys(connection,
                new("fk_leaderboard_instance_leaderboard", "leaderboard_instance", "leaderboard_id", "leaderboard", "leaderboard_id"),
                new("fk_leaderboard_entry_instance", "leaderboard_entry", "instance_id", "leaderboard_instance", "instance_id"),
                new("fk_leaderboard_meta_entry_leaderboard", "leaderboard_meta_entry", "leaderboard_id", "leaderboard", "leaderboard_id"),
                new("fk_leaderboard_reward_instance", "leaderboard_reward", "instance_id", "leaderboard_instance", "instance_id"));
        }

        private static void ValidateMetadataTable(MySqlConnection connection)
        {
            ValidateTableColumns(connection, "mhserveremu_leaderboards_schema",
                new("id", "smallint", "smallint", false),
                new("version", "int", "int", false));
        }

        private static void ValidateCheckConstraints(MySqlConnection connection)
        {
            using MySqlCommand command = new(@"
                SELECT table_constraints.table_name, check_constraints.check_clause
                FROM information_schema.table_constraints AS table_constraints
                INNER JOIN information_schema.check_constraints AS check_constraints
                    ON check_constraints.constraint_schema = table_constraints.constraint_schema
                    AND check_constraints.constraint_name = table_constraints.constraint_name
                WHERE table_constraints.constraint_schema = DATABASE()
                  AND table_constraints.table_name IN ('mhserveremu_leaderboards_schema', 'leaderboard', 'leaderboard_instance', 'leaderboard_entry', 'leaderboard_meta_entry', 'leaderboard_reward')
                  AND table_constraints.constraint_type = 'CHECK'", connection);
            using MySqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false
                || string.Equals(reader.GetString(0), "mhserveremu_leaderboards_schema", StringComparison.Ordinal) == false
                || NormalizeCheckClause(reader.GetString(1)) != "id=1"
                || reader.Read())
            {
                ThrowInconsistentSchema();
            }
        }

        private static void ValidateTableColumns(MySqlConnection connection, string tableName, params ColumnDefinition[] expectedColumns)
        {
            using MySqlCommand command = new(@"
                SELECT column_name,
                       data_type,
                       column_type,
                       is_nullable,
                       column_default,
                       character_maximum_length,
                       character_set_name,
                       collation_name
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                  AND table_name = @tableName
                ORDER BY ordinal_position", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            using MySqlDataReader reader = command.ExecuteReader();

            foreach (ColumnDefinition expectedColumn in expectedColumns)
            {
                if (reader.Read() == false
                    || string.Equals(reader.GetString(0), expectedColumn.Name, StringComparison.Ordinal) == false
                    || string.Equals(reader.GetString(1), expectedColumn.DataType, StringComparison.OrdinalIgnoreCase) == false
                    || string.Equals(NormalizeColumnType(reader.GetString(2)), expectedColumn.ColumnType, StringComparison.OrdinalIgnoreCase) == false
                    || string.Equals(reader.GetString(3), expectedColumn.Nullable ? "YES" : "NO", StringComparison.Ordinal) == false
                    || IsNullDefault(reader, 4) == false
                    || (expectedColumn.CharacterMaximumLength.HasValue
                        ? reader.IsDBNull(5) || Convert.ToInt64(reader.GetValue(5)) != expectedColumn.CharacterMaximumLength.Value
                        : reader.IsDBNull(5) == false)
                    || (expectedColumn.CharacterSetName == null
                        ? reader.IsDBNull(6) == false
                        : reader.IsDBNull(6) || string.Equals(reader.GetString(6), expectedColumn.CharacterSetName, StringComparison.OrdinalIgnoreCase) == false)
                    || (expectedColumn.CollationName == null
                        ? reader.IsDBNull(7) == false
                        : reader.IsDBNull(7) || string.Equals(reader.GetString(7), expectedColumn.CollationName, StringComparison.OrdinalIgnoreCase) == false))
                {
                    ThrowInconsistentSchema();
                }
            }

            if (reader.Read())
                ThrowInconsistentSchema();
        }

        private static void ValidateTableStorage(MySqlConnection connection, string tableName)
        {
            using MySqlCommand command = new(@"
                SELECT tables.engine, collations.character_set_name, tables.table_collation
                FROM information_schema.tables AS tables
                INNER JOIN information_schema.collations AS collations
                    ON collations.collation_name = tables.table_collation
                WHERE tables.table_schema = DATABASE()
                  AND tables.table_name = @tableName", connection);
            command.Parameters.AddWithValue("@tableName", tableName);
            using MySqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false
                || string.Equals(reader.GetString(0), "InnoDB", StringComparison.OrdinalIgnoreCase) == false
                || string.Equals(reader.GetString(1), "utf8mb4", StringComparison.OrdinalIgnoreCase) == false
                || string.Equals(reader.GetString(2), "utf8mb4_unicode_ci", StringComparison.OrdinalIgnoreCase) == false
                || reader.Read())
            {
                ThrowInconsistentSchema();
            }
        }

        private static void ValidateIndexes(MySqlConnection connection, string tableName, params IndexDefinition[] expectedIndexes)
        {
            Dictionary<string, List<(bool Unique, int Position, string ColumnName)>> actualIndexes = [];
            using (MySqlCommand command = new(@"
                SELECT index_name, non_unique, seq_in_index, column_name
                FROM information_schema.statistics
                WHERE table_schema = DATABASE()
                  AND table_name = @tableName
                ORDER BY index_name, seq_in_index", connection))
            {
                command.Parameters.AddWithValue("@tableName", tableName);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string indexName = reader.GetString(0);
                    if (actualIndexes.TryGetValue(indexName, out List<(bool Unique, int Position, string ColumnName)> columns) == false)
                    {
                        columns = [];
                        actualIndexes.Add(indexName, columns);
                    }

                    columns.Add((Convert.ToBoolean(reader.GetValue(1)) == false, Convert.ToInt32(reader.GetValue(2)), reader.GetString(3)));
                }
            }

            if (actualIndexes.Count != expectedIndexes.Length)
                ThrowInconsistentSchema();

            foreach (IndexDefinition expectedIndex in expectedIndexes)
            {
                if (actualIndexes.TryGetValue(expectedIndex.Name, out List<(bool Unique, int Position, string ColumnName)> columns) == false
                    || columns.Count != expectedIndex.Columns.Length)
                {
                    ThrowInconsistentSchema();
                }

                for (int columnIndex = 0; columnIndex < expectedIndex.Columns.Length; columnIndex++)
                {
                    (bool unique, int position, string columnName) = columns[columnIndex];
                    if (unique != expectedIndex.Unique
                        || position != columnIndex + 1
                        || string.Equals(columnName, expectedIndex.Columns[columnIndex], StringComparison.Ordinal) == false)
                    {
                        ThrowInconsistentSchema();
                    }
                }
            }
        }

        private static void ValidateForeignKeys(MySqlConnection connection, params ForeignKeyDefinition[] expectedForeignKeys)
        {
            Dictionary<string, (string TableName, string ColumnName, string ReferencedTableName, string ReferencedColumnName, string UpdateRule, string DeleteRule)> actualForeignKeys = [];
            using (MySqlCommand command = new(@"
                SELECT key_column_usage.constraint_name,
                       key_column_usage.table_name,
                       key_column_usage.column_name,
                       key_column_usage.referenced_table_name,
                       key_column_usage.referenced_column_name,
                       referential_constraints.update_rule,
                       referential_constraints.delete_rule
                FROM information_schema.key_column_usage AS key_column_usage
                INNER JOIN information_schema.referential_constraints AS referential_constraints
                    ON referential_constraints.constraint_schema = key_column_usage.constraint_schema
                    AND referential_constraints.table_name = key_column_usage.table_name
                    AND referential_constraints.constraint_name = key_column_usage.constraint_name
                WHERE key_column_usage.constraint_schema = DATABASE()
                  AND key_column_usage.table_name IN ('leaderboard', 'leaderboard_instance', 'leaderboard_entry', 'leaderboard_meta_entry', 'leaderboard_reward')
                  AND key_column_usage.referenced_table_name IS NOT NULL", connection))
            {
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    if (actualForeignKeys.TryAdd(name, (
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6))) == false)
                    {
                        ThrowInconsistentSchema();
                    }
                }
            }

            if (actualForeignKeys.Count != expectedForeignKeys.Length)
                ThrowInconsistentSchema();

            foreach (ForeignKeyDefinition expectedForeignKey in expectedForeignKeys)
            {
                if (actualForeignKeys.TryGetValue(expectedForeignKey.Name, out (string TableName, string ColumnName, string ReferencedTableName, string ReferencedColumnName, string UpdateRule, string DeleteRule) actualForeignKey) == false
                    || string.Equals(actualForeignKey.TableName, expectedForeignKey.TableName, StringComparison.Ordinal) == false
                    || string.Equals(actualForeignKey.ColumnName, expectedForeignKey.ColumnName, StringComparison.Ordinal) == false
                    || string.Equals(actualForeignKey.ReferencedTableName, expectedForeignKey.ReferencedTableName, StringComparison.Ordinal) == false
                    || string.Equals(actualForeignKey.ReferencedColumnName, expectedForeignKey.ReferencedColumnName, StringComparison.Ordinal) == false
                    || IsExpectedForeignKeyUpdateRule(actualForeignKey.UpdateRule) == false
                    || string.Equals(actualForeignKey.DeleteRule, "CASCADE", StringComparison.OrdinalIgnoreCase) == false)
                {
                    ThrowInconsistentSchema();
                }
            }
        }

        internal static bool IsExpectedForeignKeyUpdateRule(string updateRule)
        {
            return string.Equals(updateRule, "NO ACTION", StringComparison.OrdinalIgnoreCase)
                || string.Equals(updateRule, "RESTRICT", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeColumnType(string columnType)
        {
            int displayWidthStart = columnType.IndexOf('(');
            if (displayWidthStart <= 0 || columnType.EndsWith(')') == false)
                return columnType;

            string dataType = columnType[..displayWidthStart];
            return dataType.Equals("smallint", StringComparison.OrdinalIgnoreCase)
                || dataType.Equals("int", StringComparison.OrdinalIgnoreCase)
                || dataType.Equals("bigint", StringComparison.OrdinalIgnoreCase)
                ? dataType
                : columnType;
        }

        private static bool IsNullDefault(MySqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal)
                || string.Equals(reader.GetString(ordinal), "NULL", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCheckClause(string checkClause)
        {
            return new string(checkClause.Where(character => character is not (' ' or '`' or '(' or ')')).ToArray()).ToLowerInvariant();
        }

        private static void ThrowInconsistentSchema()
        {
            throw new InvalidOperationException("MySQL leaderboard tables are inconsistent with recorded schema version.");
        }

        private static int GetKnownApplicationTableCount(MySqlConnection connection)
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
                throw new InvalidOperationException("MySQL leaderboard schema initialization requires a selected database.");

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SchemaLockRole}:{databaseName}"));
            return $"{SchemaLockRole}:{Convert.ToHexString(hash)[..32]}";
        }
    }
}
