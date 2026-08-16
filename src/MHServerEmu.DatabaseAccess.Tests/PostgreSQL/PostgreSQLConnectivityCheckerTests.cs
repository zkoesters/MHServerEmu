using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLConnectivityCheckerTests
    {
        [Fact]
        public async Task CheckAsync_RetriesTwiceThenSucceeds()
        {
            int attempts = 0;
            List<TimeSpan> delays = new();
            PostgreSQLConnectivityChecker checker = new(
                (_, _, _) =>
                {
                    attempts++;
                    return attempts == 3 ? Task.CompletedTask : Task.FromException(new InvalidOperationException("password=secret"));
                },
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            PostgreSQLPersistenceFailure failure = await checker.CheckAsync(settings, CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal(3, attempts);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) }, delays);
        }

        [Fact]
        public async Task CheckAsync_AllAttemptsFail_ReturnsSanitizedFailure()
        {
            PostgreSQLConnectivityChecker checker = new(
                (_, _, _) => Task.FromException(new InvalidOperationException("Host=private Password=secret")),
                (_, _) => Task.CompletedTask);
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            PostgreSQLPersistenceFailure failure = await checker.CheckAsync(settings, CancellationToken.None);

            Assert.NotNull(failure);
            Assert.Equal("ConnectivityCheckFailed", failure.Code);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckAsync_CancellationStopsRetries()
        {
            int attempts = 0;
            using CancellationTokenSource cancellationSource = new();
            PostgreSQLConnectivityChecker checker = new(
                (_, _, _) =>
                {
                    attempts++;
                    cancellationSource.Cancel();
                    return Task.FromException(new InvalidOperationException());
                },
                (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(settings, cancellationSource.Token));
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task CheckAsync_MaximumValidStartupBackoff_ReturnsSanitizedFailure()
        {
            List<TimeSpan> delays = new();
            PostgreSQLConfig config = new()
            {
                StartupRetryCount = 3,
                StartupRetryDelayMilliseconds = 1073741823,
            };
            PostgreSQLConnectivityChecker checker = new(
                (_, _, _) => Task.FromException(new InvalidOperationException("Password=secret")),
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", config, false, out PostgreSQLSettings settings, out _));

            PostgreSQLPersistenceFailure failure = await checker.CheckAsync(settings, CancellationToken.None);

            Assert.Equal("ConnectivityCheckFailed", failure.Code);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(1073741823), TimeSpan.FromMilliseconds(2147483646) }, delays);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
