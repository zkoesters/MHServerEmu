using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Locking
{
    internal sealed class PostgreSQLWriterLockMonitor : IAsyncDisposable
    {
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
        private readonly NpgsqlConnection _connection;
        private readonly Action _fatalCallback;
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly Task _monitorTask;
        private int _fatal;

        internal PostgreSQLWriterLockMonitor(NpgsqlConnection connection, Action fatalCallback)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _fatalCallback = fatalCallback ?? throw new ArgumentNullException(nameof(fatalCallback));
            _monitorTask = MonitorAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationSource.Cancel();
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
            _cancellationSource.Dispose();
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(ProbeInterval, _cancellationSource.Token);
                    using CancellationTokenSource timeout = new(ProbeTimeout);
                    using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_cancellationSource.Token, timeout.Token);
                    await using NpgsqlCommand command = new("SELECT 1", _connection)
                    {
                        CommandTimeout = (int)ProbeTimeout.TotalSeconds,
                    };
                    await command.ExecuteScalarAsync(source.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellationSource.IsCancellationRequested)
            {
            }
            catch
            {
                if (Interlocked.Exchange(ref _fatal, 1) == 0)
                    _fatalCallback();
            }
        }
    }
}
