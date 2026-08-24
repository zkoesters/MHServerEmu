using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    internal sealed class PostgreSQLTestDatabase : IDisposable
    {
        private readonly string _operatorConnectionString;
        private readonly Action _clearPool;
        private readonly Action _dropSchema;
        private bool _disposed;

        public PostgreSQLTestDatabase()
        {
            _operatorConnectionString = Environment.GetEnvironmentVariable(PostgreSQLFactAttribute.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(_operatorConnectionString))
            {
                throw new InvalidOperationException($"{PostgreSQLFactAttribute.ConnectionStringEnvironmentVariable} must be set to create a PostgreSQL test database.");
            }

            SchemaName = $"mhserveremu_test_{Guid.NewGuid():N}";
            NpgsqlConnectionStringBuilder connectionStringBuilder = new(_operatorConnectionString)
            {
                SearchPath = SchemaName
            };
            ConnectionString = connectionStringBuilder.ConnectionString;
            _clearPool = ClearScopedPool;
            _dropSchema = DropSchema;

            using NpgsqlConnection connection = new(_operatorConnectionString);
            connection.Open();
            using NpgsqlCommand command = new($"CREATE SCHEMA {QuoteIdentifier(SchemaName)}", connection);
            command.ExecuteNonQuery();
        }

        internal PostgreSQLTestDatabase(Action clearPool, Action dropSchema)
        {
            _clearPool = clearPool;
            _dropSchema = dropSchema;
        }

        public string ConnectionString { get; }

        public string SchemaName { get; }

        public NpgsqlConnection OpenConnection()
        {
            NpgsqlConnection connection = new(ConnectionString);
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _clearPool();
            _dropSchema();
            _disposed = true;
        }

        private void ClearScopedPool()
        {
            using NpgsqlConnection connection = new(ConnectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        private void DropSchema()
        {
            using NpgsqlConnection connection = new(_operatorConnectionString);
            connection.Open();
            using NpgsqlCommand command = new($"DROP SCHEMA {QuoteIdentifier(SchemaName)} CASCADE", connection);
            command.ExecuteNonQuery();
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"\"{identifier.Replace("\"", "\"\"")}\"";
        }
    }
}
