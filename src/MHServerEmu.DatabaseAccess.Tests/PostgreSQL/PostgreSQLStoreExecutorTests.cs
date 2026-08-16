using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
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

        [PostgreSQLIntegrationFact]
        public async Task ExecuteWriteAsync_CommitCancellationSourceExpiresBeforeCommit_ReturnsFailed()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, new PostgreSQLConfig { OperationTimeoutSeconds = 1 });

            PostgreSQLWriteResult result = await fixture.Provider.StoreExecutor.ExecuteWriteAsync("AccountChange", 30, async (_, _, _) => await Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Equal(PostgreSQLWriteOutcome.Failed, result.Outcome);
        }

        [PostgreSQLIntegrationFact]
        public async Task ExecuteReadAsync_DelayedCommand_ReturnsSanitizedOperationTimeout()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, new PostgreSQLConfig { OperationTimeoutSeconds = 1 });

            PostgreSQLReadResult<bool> result = await fixture.Provider.StoreExecutor.ExecuteReadAsync("DelayedRead", async (connection, deadline, cancellationToken) =>
            {
                await using NpgsqlCommand command = new("SELECT pg_sleep(2)", connection)
                {
                    CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                };
                await command.ExecuteNonQueryAsync(cancellationToken);
                return true;
            });

            Assert.False(result.Succeeded);
            Assert.False(result.Value);
            Assert.Equal("OperationTimeout", result.Failure.Code);
            Assert.Equal("DelayedRead", result.Failure.Operation);
            Assert.DoesNotContain("Exception", result.Failure.ToString(), StringComparison.Ordinal);
        }

        [PostgreSQLIntegrationFact]
        public async Task ExecuteReadAsync_CompletedCommand_ReturnsValue()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);

            PostgreSQLReadResult<int> result = await fixture.Provider.StoreExecutor.ExecuteReadAsync("ReadOne", async (connection, deadline, cancellationToken) =>
            {
                await using NpgsqlCommand command = new("SELECT 1", connection)
                {
                    CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                };
                return (int)await command.ExecuteScalarAsync(cancellationToken);
            });

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Value);
            Assert.Null(result.Failure);
        }

        private static PostgreSQLStoreExecutor CreateFencedExecutor(PostgreSQLStoreTestFixture fixture)
        {
            fixture.Provider.FenceForTest();
            return fixture.Provider.StoreExecutor;
        }
    }
}
