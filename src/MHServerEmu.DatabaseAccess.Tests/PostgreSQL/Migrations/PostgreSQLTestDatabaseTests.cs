namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations
{
    public class PostgreSQLTestDatabaseTests
    {
        [Fact]
        public async Task DisposeAsync_WhenCleanupActionFails_ContinuesRemainingCleanupActions()
        {
            List<string> observedActions = new();
            InvalidOperationException expectedFailure = new("first cleanup failed");
            PostgreSQLTestDatabase database = new(new Func<Task>[]
            {
                () => Task.FromException(expectedFailure),
                () =>
                {
                    observedActions.Add("second");
                    return Task.CompletedTask;
                },
            });

            AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() => database.DisposeAsync());

            Assert.Equal(new[] { "second" }, observedActions);
            Assert.Equal(expectedFailure, Assert.Single(exception.InnerExceptions));
        }
    }
}
