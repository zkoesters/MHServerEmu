using System.Data;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal readonly record struct PostgreSQLSchemaResult(bool Created, int Version);

    internal static class PostgreSQLSchemaManager
    {
        private const int CurrentSchemaVersion = 7;
        private const long SchemaLockId = -6302185236071631439;

        public static PostgreSQLSchemaResult EnsureCurrentSchema(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("PostgreSQL schema initialization requires an open connection.");

            ExecuteNonQuery(connection, "SELECT pg_advisory_lock(@lockId)");
            try
            {
                return EnsureCurrentSchemaLocked(connection, initializeData);
            }
            finally
            {
                ExecuteNonQuery(connection, "SELECT pg_advisory_unlock(@lockId)");
            }
        }

        private static PostgreSQLSchemaResult EnsureCurrentSchemaLocked(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData)
        {
            if (HasTable(connection, "mhserveremu_schema") == false)
            {
                if (GetKnownApplicationTableCount(connection) > 0)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL schema metadata is missing while application tables exist.");
                }

                return InitializeSchema(connection, initializeData);
            }

            int schemaVersion = GetSchemaVersion(connection);
            if (schemaVersion < 0)
                throw new InvalidOperationException($"PostgreSQL schema version {schemaVersion} is invalid.");

            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL schema version {schemaVersion} is unsupported; current version is {CurrentSchemaVersion}.");
            }

            if (schemaVersion < CurrentSchemaVersion)
            {
                while (schemaVersion < CurrentSchemaVersion)
                {
                    ApplyMigration(connection, schemaVersion);
                    schemaVersion = GetSchemaVersion(connection);
                }
            }

            if (GetSchemaVersion(connection) != CurrentSchemaVersion)
                throw new InvalidOperationException($"PostgreSQL schema version {schemaVersion} could not be migrated to {CurrentSchemaVersion}.");

            return new PostgreSQLSchemaResult(false, schemaVersion);
        }

        internal static void ApplyMigration(NpgsqlConnection connection, int version)
        {
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using NpgsqlCommand migrationCommand = new(PostgreSQLScripts.GetMigrationScript(version), connection, transaction);
                migrationCommand.ExecuteNonQuery();

                using NpgsqlCommand versionCommand = new(
                    "UPDATE mhserveremu_schema SET version = @version WHERE id = 1", connection, transaction);
                versionCommand.Parameters.AddWithValue("version", version + 1);
                if (versionCommand.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("PostgreSQL schema metadata must contain exactly one row with id 1.");

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static PostgreSQLSchemaResult InitializeSchema(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData)
        {
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using NpgsqlCommand command = new(PostgreSQLScripts.GetInitializationScript(), connection, transaction);
                command.ExecuteNonQuery();
                initializeData?.Invoke(connection, transaction);
                transaction.Commit();
                return new PostgreSQLSchemaResult(true, CurrentSchemaVersion);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static int GetSchemaVersion(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT id, version FROM mhserveremu_schema", connection);
            using NpgsqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false)
                throw new InvalidOperationException("PostgreSQL schema metadata must contain exactly one row with id 1.");

            short schemaId = reader.GetInt16(0);
            int schemaVersion = reader.GetInt32(1);
            if (reader.Read())
                throw new InvalidOperationException("PostgreSQL schema metadata must contain exactly one row with id 1.");

            if (schemaId != 1)
                throw new InvalidOperationException("PostgreSQL schema metadata must contain exactly one row with id 1.");

            return schemaVersion;
        }

        private static int GetKnownApplicationTableCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new(@"
                SELECT COUNT(*)
                FROM unnest(ARRAY['account', 'player', 'avatar', 'team_up', 'item', 'controlled_entity', 'guild', 'guild_member']) AS table_name
                WHERE to_regclass(table_name) IS NOT NULL", connection);
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
            command.Parameters.AddWithValue("lockId", SchemaLockId);
            command.ExecuteNonQuery();
        }
    }
}
