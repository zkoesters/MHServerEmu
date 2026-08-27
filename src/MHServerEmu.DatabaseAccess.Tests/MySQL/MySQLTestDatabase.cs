using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    internal sealed class MySQLTestDatabase : IDisposable
    {
        private readonly string _operatorConnectionString;
        private readonly Action _clearPool;
        private readonly Action _dropDatabase;
        private bool _disposed;

        public MySQLTestDatabase()
        {
            _operatorConnectionString = Environment.GetEnvironmentVariable(MySQLFactAttribute.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(_operatorConnectionString))
            {
                throw new InvalidOperationException($"{MySQLFactAttribute.ConnectionStringEnvironmentVariable} must be set to create a MySQL test database.");
            }

            DatabaseName = CreateDatabaseName();
            MySqlConnectionStringBuilder connectionStringBuilder = new(_operatorConnectionString)
            {
                Database = DatabaseName
            };
            ConnectionString = connectionStringBuilder.ConnectionString;
            _clearPool = ClearScopedPool;
            _dropDatabase = DropDatabase;

            CreateDatabase();
        }

        internal MySQLTestDatabase(string operatorConnectionString, Action<string> createDatabase, Action clearPool, Action<string> dropDatabase)
        {
            _operatorConnectionString = new MySqlConnectionStringBuilder(operatorConnectionString).ConnectionString;
            DatabaseName = CreateDatabaseName();
            MySqlConnectionStringBuilder connectionStringBuilder = new(_operatorConnectionString)
            {
                Database = DatabaseName
            };
            ConnectionString = connectionStringBuilder.ConnectionString;
            _clearPool = clearPool;
            _dropDatabase = () => dropDatabase(DatabaseName);

            createDatabase(DatabaseName);
        }

        public string ConnectionString { get; }

        public string DatabaseName { get; }

        public MySqlConnection OpenConnection()
        {
            MySqlConnection connection = new(ConnectionString);
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
            _dropDatabase();
            _disposed = true;
        }

        internal static string QuoteIdentifier(string identifier)
        {
            return $"`{identifier.Replace("`", "``")}`";
        }

        private static string CreateDatabaseName()
        {
            return $"mhserveremu_test_{Guid.NewGuid():N}";
        }

        private void CreateDatabase()
        {
            using MySqlConnection connection = new(_operatorConnectionString);
            connection.Open();
            using MySqlCommand command = new($"CREATE DATABASE {QuoteIdentifier(DatabaseName)} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", connection);
            command.ExecuteNonQuery();
        }

        private void ClearScopedPool()
        {
            using MySqlConnection connection = new(ConnectionString);
            MySqlConnection.ClearPool(connection);
        }

        private void DropDatabase()
        {
            using MySqlConnection connection = new(_operatorConnectionString);
            connection.Open();
            using MySqlCommand command = new($"DROP DATABASE {QuoteIdentifier(DatabaseName)}", connection);
            command.ExecuteNonQuery();
        }
    }
}
