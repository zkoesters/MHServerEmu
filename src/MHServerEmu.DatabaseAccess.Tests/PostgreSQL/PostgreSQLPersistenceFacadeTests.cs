using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLPersistenceFacadeTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLPersistenceFacadeTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task StartAsync_ExposesFourCapabilities()
        {
            string connectionString = await _database.CreateSettingsConnectionStringAsync();
            List<PersistenceFatalFailure> failures = new();

            await using PersistenceRuntime runtime = await PostgreSQLPersistenceFacade.StartAsync(new PostgreSQLConfig(), connectionString, failures.Add);

            Assert.NotNull(runtime.Services.Accounts);
            Assert.NotNull(runtime.Services.Players);
            Assert.NotNull(runtime.Services.Guilds);
            Assert.NotNull(runtime.Services.Leaderboards);
            Assert.Empty(failures);
        }

        [PostgreSQLIntegrationFact]
        public async Task StartAsync_SecondRuntimeRejectsWriterOwnership()
        {
            string connectionString = await _database.CreateSettingsConnectionStringAsync();
            PostgreSQLConfig config = new() { OperationTimeoutSeconds = 1 };

            await using PersistenceRuntime first = await PostgreSQLPersistenceFacade.StartAsync(config, connectionString, _ => { });

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSQLPersistenceFacade.StartAsync(config, connectionString, _ => { }));

            Assert.Contains("writer_lock_timeout", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task StartAsync_InvalidSettingsFailsWithoutInvokingFatalCallback()
        {
            bool callbackInvoked = false;

            await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSQLPersistenceFacade.StartAsync(new PostgreSQLConfig(), string.Empty, _ => callbackInvoked = true));

            Assert.False(callbackInvoked);
        }
    }
}
