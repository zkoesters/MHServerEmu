using System.Data;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal readonly record struct PostgreSQLLeaderboardSchemaResult(bool Created, int Version);

    internal static class PostgreSQLLeaderboardSchemaManager
    {
        private const int CurrentSchemaVersion = 1;
        private const long SchemaLockId = -6302185236071631440;

        public static PostgreSQLLeaderboardSchemaResult EnsureCurrentSchema(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("PostgreSQL leaderboard schema initialization requires an open connection.");

            ExecuteLockCommand(connection, "SELECT pg_advisory_lock(@lockId)");
            try
            {
                return EnsureCurrentSchemaLocked(connection, initializeData);
            }
            finally
            {
                ExecuteLockCommand(connection, "SELECT pg_advisory_unlock(@lockId)");
            }
        }

        private static PostgreSQLLeaderboardSchemaResult EnsureCurrentSchemaLocked(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData)
        {
            if (HasTable(connection, "mhserveremu_leaderboards_schema") == false)
            {
                if (GetKnownLeaderboardTableCount(connection) > 0)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL leaderboard schema metadata is missing while leaderboard tables exist.");
                }

                return InitializeSchema(connection, initializeData);
            }

            int schemaVersion = GetSchemaVersion(connection);
            if (schemaVersion < 0)
                throw new InvalidOperationException($"PostgreSQL leaderboard schema version {schemaVersion} is invalid.");

            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL leaderboard schema version {schemaVersion} is unsupported; current version is {CurrentSchemaVersion}.");
            }

            if (schemaVersion < CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL leaderboard schema version {schemaVersion} has no migration path to {CurrentSchemaVersion}.");
            }

            if (GetKnownLeaderboardTableCount(connection) != 5)
            {
                throw new InvalidOperationException(
                    "PostgreSQL leaderboard schema version 1 is missing one or more required tables.");
            }

            return new PostgreSQLLeaderboardSchemaResult(false, schemaVersion);
        }

        private static PostgreSQLLeaderboardSchemaResult InitializeSchema(
            NpgsqlConnection connection,
            Action<NpgsqlConnection, NpgsqlTransaction> initializeData)
        {
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using NpgsqlCommand command = new(PostgreSQLScripts.GetLeaderboardInitializationScript(), connection, transaction);
                command.ExecuteNonQuery();
                initializeData?.Invoke(connection, transaction);
                transaction.Commit();
                return new PostgreSQLLeaderboardSchemaResult(true, CurrentSchemaVersion);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static int GetSchemaVersion(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT id, version FROM mhserveremu_leaderboards_schema", connection);
            using NpgsqlDataReader reader = command.ExecuteReader();

            if (reader.Read() == false)
                throw new InvalidOperationException("PostgreSQL leaderboard schema metadata must contain exactly one row with id 1.");

            short schemaId = reader.GetInt16(0);
            int schemaVersion = reader.GetInt32(1);
            if (reader.Read() || schemaId != 1)
                throw new InvalidOperationException("PostgreSQL leaderboard schema metadata must contain exactly one row with id 1.");

            return schemaVersion;
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

        private static bool HasTable(NpgsqlConnection connection, string tableName)
        {
            using NpgsqlCommand command = new("SELECT to_regclass(@tableName) IS NOT NULL", connection);
            command.Parameters.AddWithValue("tableName", tableName);
            return (bool)command.ExecuteScalar();
        }

        private static void ExecuteLockCommand(NpgsqlConnection connection, string commandText)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.Parameters.AddWithValue("lockId", SchemaLockId);
            command.ExecuteNonQuery();
        }
    }
}
