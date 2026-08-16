using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLConnectivityChecker
    {
        private readonly Func<PostgreSQLSettings, PostgreSQLOperationDeadline, CancellationToken, Task> _attemptAsync;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        internal PostgreSQLConnectivityChecker()
            : this(AttemptAsync, (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
        {
        }

        internal PostgreSQLConnectivityChecker(
            Func<PostgreSQLSettings, PostgreSQLOperationDeadline, CancellationToken, Task> attemptAsync,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _attemptAsync = attemptAsync ?? throw new ArgumentNullException(nameof(attemptAsync));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        internal async Task<PostgreSQLPersistenceFailure> CheckAsync(PostgreSQLSettings settings, CancellationToken cancellationToken)
        {
            PostgreSQLPersistenceFailure failure = null;
            for (int attempt = 0; attempt < settings.StartupRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PostgreSQLOperationDeadline deadline = new(TimeSpan.FromSeconds(settings.OperationTimeoutSeconds));
                    await _attemptAsync(settings, deadline, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    failure = new("ConnectivityCheckFailed", "ConnectivityCheck");
                }

                if (attempt + 1 < settings.StartupRetryCount)
                    await _delayAsync(TimeSpan.FromMilliseconds((long)settings.StartupRetryDelayMilliseconds * (1L << attempt)), cancellationToken);
            }

            return failure;
        }

        private static async Task AttemptAsync(PostgreSQLSettings settings, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            await using NpgsqlDataSource dataSource = PostgreSQLDataSourceFactory.Build(settings);
            using CancellationTokenSource cancellationSource = deadline.CreateCancellationSource(cancellationToken);
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationSource.Token);
            await using NpgsqlCommand command = dataSource.CreateCommand("SELECT 1");
            command.Connection = connection;
            command.CommandTimeout = deadline.RemainingCommandTimeoutSeconds;
            await command.ExecuteScalarAsync(cancellationSource.Token);
        }
    }
}
