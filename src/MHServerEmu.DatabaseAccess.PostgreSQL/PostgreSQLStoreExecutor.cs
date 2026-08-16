using System.Data;
using MHServerEmu.DatabaseAccess.PostgreSQL.Locking;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLStoreExecutor
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgreSQLWriterOwner _writerOwner;
        private readonly TimeSpan _operationTimeout;
        private readonly IPostgreSQLTransactionCommitter _committer;

        internal PostgreSQLStoreExecutor(NpgsqlDataSource dataSource, PostgreSQLWriterOwner writerOwner, TimeSpan operationTimeout, IPostgreSQLTransactionCommitter committer)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _writerOwner = writerOwner ?? throw new ArgumentNullException(nameof(writerOwner));
            if (operationTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(operationTimeout));
            _operationTimeout = operationTimeout;
            _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        }

        internal async Task<PostgreSQLWriteResult> ExecuteWriteAsync(string operation, long entityId, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(writeAsync);

            PostgreSQLOperationDeadline deadline = new(_operationTimeout);
            bool commitStarted = false;
            try
            {
                using (CancellationTokenSource connectionCancellation = deadline.CreateCancellationSource(cancellationToken))
                await using (NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(connectionCancellation.Token))
                {
                    using (CancellationTokenSource transactionCancellation = deadline.CreateCancellationSource(cancellationToken))
                    {
                        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, transactionCancellation.Token);
                        try
                        {
                            await ConfigureTimeoutsAsync(connection, transaction, deadline, cancellationToken);
                            await _writerOwner.ValidateTransactionAsync(connection, transaction, _writerOwner.FenceToken, deadline, cancellationToken);
                            using (CancellationTokenSource writeCancellation = deadline.CreateCancellationSource(cancellationToken))
                            await writeAsync(connection, transaction, writeCancellation.Token);

                            commitStarted = true;
                            using (CancellationTokenSource commitCancellation = deadline.CreateCancellationSource(cancellationToken))
                            await _committer.CommitAsync(transaction, commitCancellation.Token);

                            return PostgreSQLWriteResult.Success();
                        }
                        catch (Exception exception)
                        {
                            if (commitStarted == false)
                                await RollbackAsync(transaction, deadline, cancellationToken);

                            return WithEntityId(ClassifyFailure(operation, commitStarted, exception), entityId);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                return WithEntityId(ClassifyFailure(operation, commitStarted, exception), entityId);
            }
        }

        internal static PostgreSQLWriteResult ClassifyFailure(string operation, bool commitStarted, Exception exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(exception);

            string code = commitStarted
                ? "WriteOutcomeUncertain"
                : exception switch
                {
                    PostgreSQLWriterFencedException => "WriterFenced",
                    TimeoutException or OperationCanceledException => "OperationTimeout",
                    _ => "WriteFailed",
                };
            string sqlState = exception is PostgresException postgresException ? postgresException.SqlState : null;
            PostgreSQLWriteOutcome outcome = commitStarted ? PostgreSQLWriteOutcome.OutcomeUncertain : PostgreSQLWriteOutcome.Failed;
            return new PostgreSQLWriteResult(outcome, new PostgreSQLPersistenceFailure(code, operation, sqlState));
        }

        private static async Task ConfigureTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            string remaining = ToMilliseconds(deadline.Remaining);
            await using NpgsqlCommand command = new("SELECT set_config('statement_timeout', @statementTimeout, true); SELECT set_config('lock_timeout', @lockTimeout, true)", connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            command.Parameters.AddWithValue("statementTimeout", remaining);
            command.Parameters.AddWithValue("lockTimeout", remaining);
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            await command.ExecuteNonQueryAsync(source.Token);
        }

        private static async Task RollbackAsync(NpgsqlTransaction transaction, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            try
            {
                using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
                await transaction.RollbackAsync(source.Token);
            }
            catch
            {
            }
        }

        private static PostgreSQLWriteResult WithEntityId(PostgreSQLWriteResult result, long entityId)
        {
            PostgreSQLPersistenceFailure failure = result.Failure;
            return new PostgreSQLWriteResult(result.Outcome, new PostgreSQLPersistenceFailure(failure.Code, failure.Operation, failure.SqlState, failure.MigrationIdentity, entityId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static string ToMilliseconds(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new TimeoutException();
            return $"{Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds))}ms";
        }
    }
}
