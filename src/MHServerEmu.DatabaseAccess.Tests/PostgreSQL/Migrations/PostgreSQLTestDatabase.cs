using System.Text.RegularExpressions;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations
{
    public sealed class PostgreSQLIntegrationFactAttribute : FactAttribute
    {
        public PostgreSQLIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING")))
                Skip = "MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING is not configured.";
        }
    }

    public sealed class PostgreSQLIntegrationTheoryAttribute : TheoryAttribute
    {
        public PostgreSQLIntegrationTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING")))
                Skip = "MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING is not configured.";
        }
    }

    public sealed class PostgreSQLTestDatabase : IAsyncLifetime
    {
        private const string AdminConnectionStringVariable = "MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING";
        private static readonly Regex DatabaseNamePattern = new("^mhserveremu_test_[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private readonly List<(string Name, NpgsqlDataSource DataSource)> _databases = new();
        private readonly string _adminConnectionString;

        public PostgreSQLTestDatabase()
        {
            _adminConnectionString = Environment.GetEnvironmentVariable(AdminConnectionStringVariable);
            if (string.IsNullOrWhiteSpace(_adminConnectionString))
                return;

            NpgsqlConnectionStringBuilder builder = new(_adminConnectionString);
            IsAvailable = string.IsNullOrWhiteSpace(builder.Host) == false && string.IsNullOrWhiteSpace(builder.Username) == false;
        }

        public bool IsAvailable { get; }

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task<NpgsqlDataSource> CreateDataSourceAsync()
        {
            if (IsAvailable == false)
                throw new InvalidOperationException($"{AdminConnectionStringVariable} is not configured.");

            string databaseName = $"mhserveremu_test_{Guid.NewGuid():N}";
            await using (NpgsqlConnection adminConnection = new(_adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using NpgsqlCommand command = new($"CREATE DATABASE \"{databaseName}\"", adminConnection);
                await command.ExecuteNonQueryAsync();
            }

            NpgsqlConnectionStringBuilder databaseBuilder = new(_adminConnectionString)
            {
                Database = databaseName,
                Pooling = true,
                IncludeErrorDetail = false,
                PersistSecurityInfo = false,
            };
            NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(databaseBuilder.ConnectionString).Build();
            _databases.Add((databaseName, dataSource));
            return dataSource;
        }

        public async Task DisposeAsync()
        {
            foreach ((string name, NpgsqlDataSource dataSource) in _databases)
            {
                await dataSource.DisposeAsync();
                await DropDatabaseAsync(name);
            }
        }

        private async Task DropDatabaseAsync(string databaseName)
        {
            if (DatabaseNamePattern.IsMatch(databaseName) == false)
                throw new InvalidOperationException("Refusing to drop a database outside the test database naming convention.");

            await using NpgsqlConnection adminConnection = new(_adminConnectionString);
            await adminConnection.OpenAsync();
            await using (NpgsqlCommand terminate = new("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid()", adminConnection))
            {
                terminate.Parameters.AddWithValue("databaseName", databaseName);
                await terminate.ExecuteNonQueryAsync();
            }
            await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{databaseName}\"", adminConnection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [CollectionDefinition("PostgreSQL migration integration", DisableParallelization = true)]
    public sealed class PostgreSQLMigrationIntegrationCollection : ICollectionFixture<PostgreSQLTestDatabase>
    {
    }
}
