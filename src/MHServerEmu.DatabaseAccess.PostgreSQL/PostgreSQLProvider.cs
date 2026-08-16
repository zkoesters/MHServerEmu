using System.Data;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.PostgreSQL.Locking;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLProvider : IAsyncDisposable
    {
        private readonly PostgreSQLSettings _settings;
        private readonly PostgreSQLMigrationCatalog _migrationCatalog;
        private readonly TimeSpan _writerLockTimeout;
        private readonly Action<PostgreSQLPersistenceFailure> _fatalCallback;
        private NpgsqlDataSource _dataSource;
        private PostgreSQLWriterOwner _writerOwner;
        private PostgreSQLWriterLockMonitor _monitor;
        private int _started;

        internal PostgreSQLProvider(PostgreSQLSettings settings, PostgreSQLMigrationCatalog migrationCatalog, TimeSpan? writerLockTimeout = null, Action<PostgreSQLPersistenceFailure> fatalCallback = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _migrationCatalog = migrationCatalog ?? throw new ArgumentNullException(nameof(migrationCatalog));
            _writerLockTimeout = writerLockTimeout ?? TimeSpan.FromSeconds(_settings.OperationTimeoutSeconds);
            if (_writerLockTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(writerLockTimeout));
            _fatalCallback = fatalCallback ?? (_ => { });
        }

        internal NpgsqlDataSource DataSource => _dataSource;
        internal PostgreSQLWriterFenceToken WriterFenceToken => _writerOwner?.FenceToken;
        internal int WriterBackendProcessId => _writerOwner?.BackendProcessId ?? 0;
        internal bool IsFenced => _writerOwner?.IsFenced ?? true;

        internal async Task<PostgreSQLProviderStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                return PostgreSQLProviderStartResult.Failed(new PostgreSQLPersistenceFailure("ProviderAlreadyStarted", "ProviderStart"));

            NpgsqlConnection writerConnection = null;
            try
            {
                _dataSource = PostgreSQLDataSourceFactory.Build(_settings);
                PostgreSQLPersistenceFailure connectivityFailure = await CheckConnectivityAsync(cancellationToken);
                if (connectivityFailure != null)
                    return await FailedStartAsync(connectivityFailure);

                writerConnection = PostgreSQLDataSourceFactory.BuildWriterConnection(_settings);
                await writerConnection.OpenAsync(cancellationToken);
                _writerOwner = await PostgreSQLWriterOwner.CreateAsync(
                    writerConnection,
                    _writerLockTimeout,
                    RunMigrationsAsync,
                    cancellationToken);
                writerConnection = null;
                _monitor = new PostgreSQLWriterLockMonitor(_writerOwner.Connection, Fence);
                return PostgreSQLProviderStartResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposePartialAsync(writerConnection);
                throw;
            }
            catch (PostgreSQLWriterOwner.PostgreSQLWriterOwnerStartException exception)
            {
                return await FailedStartAsync(exception.Failure, writerConnection);
            }
            catch
            {
                return await FailedStartAsync(new PostgreSQLPersistenceFailure("ProviderStartFailed", "ProviderStart"), writerConnection);
            }
        }

        internal async Task ValidateWriterAsync(PostgreSQLWriterFenceToken token, CancellationToken cancellationToken = default)
        {
            PostgreSQLWriterOwner owner = _writerOwner ?? throw new PostgreSQLWriterFencedException();
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await owner.ValidateTransactionAsync(connection, transaction, token, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }

        internal async Task ExecuteWriterTransactionAsync(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(writeAsync);
            PostgreSQLWriterOwner owner = _writerOwner ?? throw new PostgreSQLWriterFencedException();
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await owner.ValidateTransactionAsync(connection, transaction, owner.FenceToken, cancellationToken);
            await writeAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_monitor != null)
                await _monitor.DisposeAsync();
            if (_writerOwner != null)
                await _writerOwner.DisposeAsync();
            if (_dataSource != null)
                await _dataSource.DisposeAsync();
            _monitor = null;
            _writerOwner = null;
            _dataSource = null;
        }

        private async Task<PostgreSQLPersistenceFailure> CheckConnectivityAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < _settings.StartupRetryCount; attempt++)
            {
                try
                {
                    await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                    await using NpgsqlCommand command = new("SELECT 1", connection);
                    await command.ExecuteScalarAsync(cancellationToken);
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    if (attempt + 1 < _settings.StartupRetryCount)
                        await Task.Delay(TimeSpan.FromMilliseconds((long)_settings.StartupRetryDelayMilliseconds * (1L << attempt)), cancellationToken);
                }
            }

            return new PostgreSQLPersistenceFailure("ConnectivityCheckFailed", "ConnectivityCheck");
        }

        private Task<PostgreSQLMigrationResult> RunMigrationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            return new PostgreSQLMigrationRunner(
                _dataSource,
                _migrationCatalog,
                TimeSpan.FromSeconds(_settings.MigrationTimeoutSeconds),
                TimeSpan.FromMilliseconds(_settings.MigrationLockTimeoutMilliseconds)).RunAsync(connection, cancellationToken);
        }

        private async Task<PostgreSQLProviderStartResult> FailedStartAsync(PostgreSQLPersistenceFailure failure, NpgsqlConnection writerConnection = null)
        {
            await DisposePartialAsync(writerConnection);
            return PostgreSQLProviderStartResult.Failed(failure);
        }

        private async Task DisposePartialAsync(NpgsqlConnection writerConnection)
        {
            if (writerConnection != null)
                await writerConnection.DisposeAsync();
            if (_dataSource != null)
                await _dataSource.DisposeAsync();
            _dataSource = null;
        }

        private void Fence()
        {
            _writerOwner.Fence();
            _fatalCallback(new PostgreSQLPersistenceFailure("WriterLockLost", "WriterMonitor"));
        }
    }
}
