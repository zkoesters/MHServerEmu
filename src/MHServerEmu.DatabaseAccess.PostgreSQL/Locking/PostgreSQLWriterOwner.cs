using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Locking
{
    internal sealed class PostgreSQLWriterOwner : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private int _fenced;

        internal PostgreSQLWriterOwner(NpgsqlConnection connection, PostgreSQLWriterFenceToken fenceToken, int backendProcessId)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            FenceToken = fenceToken ?? throw new ArgumentNullException(nameof(fenceToken));
            BackendProcessId = backendProcessId;
        }

        internal PostgreSQLWriterFenceToken FenceToken { get; }
        internal int BackendProcessId { get; }
        internal bool IsFenced => Volatile.Read(ref _fenced) != 0;
        internal NpgsqlConnection Connection => _connection;

        internal void Fence()
        {
            Interlocked.Exchange(ref _fenced, 1);
        }

        internal static async Task<PostgreSQLWriterOwner> CreateAsync(
            NpgsqlConnection connection,
            TimeSpan writerLockTimeout,
            Func<NpgsqlConnection, CancellationToken, Task<PostgreSQLMigrationResult>> runMigrationsAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(runMigrationsAsync);
            PostgreSQLOperationDeadline deadline = new(writerLockTimeout);
            await AcquireExclusiveWriterLockAsync(connection, deadline, cancellationToken);
            PostgreSQLMigrationResult migrationResult = await runMigrationsAsync(connection, cancellationToken);
            if (migrationResult.Succeeded == false)
                throw new PostgreSQLWriterOwnerStartException(migrationResult.Failure);

            PostgreSQLWriterFenceToken fenceToken = await ClaimFenceAsync(connection, cancellationToken);
            await AcquireSharedSessionLockAsync(connection, PostgreSQLAdvisoryKeys.WriterResource, cancellationToken);
            await AcquireSharedSessionLockAsync(connection, PostgreSQLAdvisoryKeys.MigrationResource, cancellationToken);
            await ReleaseExclusiveWriterLockAsync(connection, cancellationToken);
            return new PostgreSQLWriterOwner(connection, fenceToken, await GetBackendProcessIdAsync(connection, cancellationToken));
        }

        internal async Task ValidateTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLWriterFenceToken token, CancellationToken cancellationToken)
        {
            if (IsFenced || token == null || token.OwnerId != FenceToken.OwnerId || token.Generation != FenceToken.Generation)
                throw new PostgreSQLWriterFencedException();

            await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_xact_lock_shared(@namespace, @resource)", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("namespace", PostgreSQLAdvisoryKeys.Namespace);
                lockCommand.Parameters.AddWithValue("resource", PostgreSQLAdvisoryKeys.WriterResource);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using NpgsqlCommand validationCommand = new("SELECT 1 FROM mhserveremu.writer_fence WHERE singleton = true AND generation = @generation AND owner_id = @ownerId", connection, transaction);
            validationCommand.Parameters.AddWithValue("generation", token.Generation);
            validationCommand.Parameters.AddWithValue("ownerId", token.OwnerId);
            if (await validationCommand.ExecuteScalarAsync(cancellationToken) == null)
                throw new PostgreSQLWriterFencedException();
        }

        public ValueTask DisposeAsync()
        {
            return _connection.DisposeAsync();
        }

        private static async Task AcquireExclusiveWriterLockAsync(NpgsqlConnection connection, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            while (deadline.Remaining > TimeSpan.Zero)
            {
                await using NpgsqlCommand command = new("SELECT pg_try_advisory_lock(@namespace, @resource)", connection)
                {
                    CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                };
                command.Parameters.AddWithValue("namespace", PostgreSQLAdvisoryKeys.Namespace);
                command.Parameters.AddWithValue("resource", PostgreSQLAdvisoryKeys.WriterResource);
                try
                {
                    using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
                    if ((bool)await command.ExecuteScalarAsync(source.Token))
                        return;
                }
                catch (TimeoutException)
                {
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, deadline.Remaining.TotalMilliseconds)), cancellationToken);
            }

            throw new PostgreSQLWriterOwnerStartException(new PostgreSQLPersistenceFailure("writer_lock_timeout", "WriterLock"));
        }

        private static async Task AcquireSharedSessionLockAsync(NpgsqlConnection connection, int resource, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT pg_advisory_lock_shared(@namespace, @resource)", connection);
            command.Parameters.AddWithValue("namespace", PostgreSQLAdvisoryKeys.Namespace);
            command.Parameters.AddWithValue("resource", resource);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task ReleaseExclusiveWriterLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT pg_advisory_unlock(@namespace, @resource)", connection);
            command.Parameters.AddWithValue("namespace", PostgreSQLAdvisoryKeys.Namespace);
            command.Parameters.AddWithValue("resource", PostgreSQLAdvisoryKeys.WriterResource);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<PostgreSQLWriterFenceToken> ClaimFenceAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            Guid ownerId = Guid.NewGuid();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = new("UPDATE mhserveremu.writer_fence SET generation = generation + 1, owner_id = @ownerId WHERE singleton = true RETURNING generation", connection, transaction);
            command.Parameters.AddWithValue("ownerId", ownerId);
            long generation = (long)await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PostgreSQLWriterFenceToken(ownerId, generation);
        }

        private static async Task<int> GetBackendProcessIdAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT pg_backend_pid()", connection);
            return (int)await command.ExecuteScalarAsync(cancellationToken);
        }

        internal sealed class PostgreSQLWriterOwnerStartException : Exception
        {
            internal PostgreSQLWriterOwnerStartException(PostgreSQLPersistenceFailure failure)
            {
                Failure = failure ?? throw new ArgumentNullException(nameof(failure));
            }

            internal PostgreSQLPersistenceFailure Failure { get; }
        }
    }
}
