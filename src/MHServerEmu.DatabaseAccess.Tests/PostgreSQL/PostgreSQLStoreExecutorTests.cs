using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLStoreExecutorTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLStoreExecutorTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [Fact]
        public void ClassifyCommitFailure_CommitStarted_ReturnsOutcomeUncertain()
        {
            PostgreSQLWriteResult result = PostgreSQLStoreExecutor.ClassifyFailure("AccountChange", commitStarted: true, new NpgsqlException());

            Assert.Equal(PostgreSQLWriteOutcome.OutcomeUncertain, result.Outcome);
        }

        [PostgreSQLIntegrationFact]
        public async Task ExecuteWriteAsync_FencedWriter_DoesNotInvokeCallback()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            PostgreSQLStoreExecutor executor = CreateFencedExecutor(fixture);
            bool called = false;

            PostgreSQLWriteResult result = await executor.ExecuteWriteAsync("AccountChange", 30, (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });

            Assert.False(called);
            Assert.Equal(PostgreSQLWriteOutcome.Failed, result.Outcome);
        }

        private static PostgreSQLStoreExecutor CreateFencedExecutor(PostgreSQLStoreTestFixture fixture)
        {
            fixture.Provider.FenceForTest();
            return fixture.Provider.StoreExecutor;
        }
    }
}
