using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Locking;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Locking
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLWriterBootstrapTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLWriterBootstrapTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [Fact]
        public void AdvisoryKeys_UseTheMHServerEmuNamespace()
        {
            Assert.Equal(0x4D485345, PostgreSQLAdvisoryKeys.Namespace);
            Assert.Equal(1, PostgreSQLAdvisoryKeys.WriterResource);
            Assert.Equal(2, PostgreSQLAdvisoryKeys.MigrationResource);
        }

        [Fact]
        public async Task StartAsync_ConnectivityFailureIsSanitizedAndDisposeIsIdempotent()
        {
            PostgreSQLConfig config = new()
            {
                StartupRetryCount = 1,
            };
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost;Port=1", config, false, out PostgreSQLSettings settings, out _));
            PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());

            PostgreSQLProviderStartResult result = await provider.StartAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("ConnectivityCheckFailed", result.Failure.Code);
            Assert.DoesNotContain("localhost", result.Failure.ToString(), StringComparison.OrdinalIgnoreCase);
            await provider.DisposeAsync();
            await provider.DisposeAsync();
        }

        [PostgreSQLIntegrationFact]
        public async Task StartAsync_ClaimsFenceAndRejectsSecondWriter()
        {
            PostgreSQLSettings settings = await CreateSettingsAsync();
            await using PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());

            PostgreSQLProviderStartResult first = await provider.StartAsync();

            Assert.True(first.Succeeded);
            Assert.NotNull(provider.WriterFenceToken);
            Assert.Equal(1L, provider.WriterFenceToken.Generation);
            await using PostgreSQLProvider contender = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromMilliseconds(100));
            PostgreSQLProviderStartResult second = await contender.StartAsync();
            Assert.False(second.Succeeded);
            Assert.Equal("writer_lock_timeout", second.Failure.Code);
            Assert.Equal(provider.WriterFenceToken.OwnerId, await ScalarAsync<Guid>(provider.DataSource, "SELECT owner_id FROM mhserveremu.writer_fence WHERE singleton = true"));
        }

        [PostgreSQLIntegrationFact]
        public async Task ConnectionLoss_FencesOldWriterAndAllowsReplacement()
        {
            PostgreSQLSettings settings = await CreateSettingsAsync();
            int fatalCount = 0;
            await using PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded(), fatalCallback: _ => Interlocked.Increment(ref fatalCount));
            Assert.True((await provider.StartAsync()).Succeeded);
            PostgreSQLWriterFenceToken oldToken = provider.WriterFenceToken;
            await TerminateBackendAsync(provider.WriterBackendProcessId);
            await WaitUntilAsync(() => provider.IsFenced, TimeSpan.FromSeconds(15));

            await using PostgreSQLProvider replacement = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());
            Assert.True((await replacement.StartAsync()).Succeeded);

            await Assert.ThrowsAsync<PostgreSQLWriterFencedException>(() => provider.ValidateWriterAsync(oldToken));
            Assert.Equal(1, fatalCount);
            Assert.True(replacement.WriterFenceToken.Generation > oldToken.Generation);
        }

        [PostgreSQLIntegrationFact]
        public async Task MigrationRunner_CannotRunAgainstActiveProvider()
        {
            PostgreSQLSettings settings = await CreateSettingsAsync();
            await using PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());
            Assert.True((await provider.StartAsync()).Succeeded);

            PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(
                provider.DataSource,
                PostgreSQLMigrationCatalog.LoadEmbedded(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(100)).RunAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("migration_lock_timeout", result.Failure.Code);
        }

        [PostgreSQLIntegrationFact]
        public async Task ExecuteWriterTransactionAsync_ReadCommittedRejectsOldFenceAfterReplacement()
        {
            PostgreSQLSettings settings = await CreateSettingsAsync();
            await SetDefaultTransactionIsolationAsync(settings, "repeatable read");
            await using PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());
            Assert.True((await provider.StartAsync()).Succeeded);
            PostgreSQLWriterFenceToken oldToken = provider.WriterFenceToken;
            await TerminateBackendAsync(provider.WriterBackendProcessId);

            await using NpgsqlConnection replacement = PostgreSQLDataSourceFactory.BuildWriterConnection(settings);
            await replacement.OpenAsync();
            await ExecuteAdvisoryLockAsync(replacement, "SELECT pg_advisory_lock(@namespace, @resource)");
            bool protectedWriteExecuted = false;
            Task protectedWrite = provider.ExecuteWriterTransactionAsync((_, _, _) =>
            {
                protectedWriteExecuted = true;
                return Task.CompletedTask;
            });

            await using (NpgsqlTransaction transaction = await replacement.BeginTransactionAsync())
            {
                await using NpgsqlCommand claim = new("UPDATE mhserveremu.writer_fence SET generation = generation + 1, owner_id = @ownerId WHERE singleton = true", replacement, transaction);
                claim.Parameters.AddWithValue("ownerId", Guid.NewGuid());
                await claim.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await ExecuteAdvisoryLockAsync(replacement, "SELECT pg_advisory_unlock(@namespace, @resource)");

            await Assert.ThrowsAsync<PostgreSQLWriterFencedException>(() => protectedWrite);
            Assert.False(protectedWriteExecuted);
            Assert.Equal(oldToken.Generation + 1, await ScalarAsync<long>(provider.DataSource, "SELECT generation FROM mhserveremu.writer_fence WHERE singleton = true"));
        }

        private async Task<PostgreSQLSettings> CreateSettingsAsync()
        {
            string connectionString = await _database.CreateSettingsConnectionStringAsync();
            Assert.True(PostgreSQLSettings.TryCreate(connectionString, new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));
            return settings;
        }

        private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(sql);
            return (T)await command.ExecuteScalarAsync();
        }

        private static async Task TerminateBackendAsync(int processId)
        {
            await using NpgsqlConnection connection = new(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING"));
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT pg_terminate_backend(@processId)", connection);
            command.Parameters.AddWithValue("processId", processId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task SetDefaultTransactionIsolationAsync(PostgreSQLSettings settings, string isolationLevel)
        {
            NpgsqlConnectionStringBuilder builder = new(settings.ConnectionString);
            await using NpgsqlConnection connection = new(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING"));
            await connection.OpenAsync();
            await using NpgsqlCommand command = new($"ALTER DATABASE \"{builder.Database}\" SET default_transaction_isolation = '{isolationLevel}'", connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ExecuteAdvisoryLockAsync(NpgsqlConnection connection, string sql)
        {
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("namespace", PostgreSQLAdvisoryKeys.Namespace);
            command.Parameters.AddWithValue("resource", PostgreSQLAdvisoryKeys.WriterResource);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            using CancellationTokenSource cancellationSource = new(timeout);
            while (condition() == false)
                await Task.Delay(50, cancellationSource.Token);
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            using CancellationTokenSource cancellationSource = new(timeout);
            while (await condition() == false)
                await Task.Delay(50, cancellationSource.Token);
        }
    }
}
